using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;
using Pongsole.Core;

namespace Pongsole.Server.Tests;

public sealed class GameCoordinatorTests
{
    private readonly FakeTimeProvider time = new();
    private readonly GameEngine engine = new(1);
    private GameCoordinator NewCoordinator() => new(engine, time, new Random(2));

    [Fact]
    public async Task LatencyExpiresAndNeverSurvivesAConnectionLease()
    {
        using var coordinator = NewCoordinator();
        var original = coordinator.Admit("player");
        var join = await Complete(coordinator, coordinator.JoinAsync(original, "Player", null));
        Assert.Null(Assert.Single(join.Snapshot.Players).LatencyMilliseconds);

        coordinator.UpdateLatency(original, 24, time.GetTimestamp());
        var observed = await ReadSnapshot(coordinator, "player");
        Assert.Equal(24, Assert.Single(observed.Players).LatencyMilliseconds);

        time.Advance(TimeSpan.FromSeconds(6));
        observed = await ReadSnapshot(coordinator, "player");
        Assert.Null(Assert.Single(observed.Players).LatencyMilliseconds);

        await Complete(coordinator, coordinator.DisconnectAsync(original));
        var replacement = coordinator.Admit("player");
        var resumed = await Complete(coordinator, coordinator.JoinAsync(replacement, "Player", join.ResumeToken));
        Assert.Equal(join.PlayerId, resumed.PlayerId);
        Assert.Null(Assert.Single(resumed.Snapshot.Players).LatencyMilliseconds);

        coordinator.UpdateLatency(original, 58, time.GetTimestamp());
        observed = await ReadSnapshot(coordinator, "player");
        Assert.Null(Assert.Single(observed.Players).LatencyMilliseconds);
    }


    [Fact]
    public async Task OldLeaseCommandsCannotAffectResumedSession()
    {
        using var coordinator = NewCoordinator();
        var oldLease = coordinator.Admit("reused");
        var original = await Complete(coordinator, coordinator.JoinAsync(oldLease, "Player", null));
        var snapshots = coordinator.Subscribe("reused");
        var oldPush = coordinator.PushAsync(oldLease, Direction.Down, 100);
        var disconnect = coordinator.DisconnectAsync(oldLease);
        coordinator.Tick();
        await Task.WhenAll(oldPush, disconnect);
        await snapshots.Completion;

        var resumedLease = coordinator.Admit("reused");
        var resumed = await Complete(coordinator, coordinator.JoinAsync(resumedLease, "Changed", original.ResumeToken));
        Assert.Equal(original.PlayerId, resumed.PlayerId);
        Assert.Equal(0, resumed.LastProcessedSequence);
        Assert.Equal("Player", Assert.Single(resumed.Snapshot.Players).DisplayName);
        await coordinator.DisconnectAsync(oldLease);
        await Error("NOT_JOINED", () => coordinator.PushAsync(oldLease, Direction.Down, 200));

        await Complete(coordinator, coordinator.PushAsync(resumedLease, Direction.Up, 1));
        AdvanceToSnapshot(coordinator);
        var current = await coordinator.Subscribe("reused").ReadAsync(TestContext.Current.CancellationToken);
        var player = Assert.Single(current.Players);
        Assert.Equal(1, player.LastProcessedSequence);
        Assert.Equal(0, player.RecentPushCount);
        Assert.Null(player.LastUpTick);
        Assert.Null(player.LastDownTick);
        Assert.Equal(Phase.Waiting, current.Arena.Phase);
    }

    [Fact]
    public async Task InputOverloadAndInvalidCommandsDoNotAdvanceSequence()
    {
        using var coordinator = NewCoordinator();
        coordinator.Admit("player");
        await Complete(coordinator, coordinator.JoinAsync("player", "Name", null));

        var pending = Enumerable.Range(1, 1024)
            .Select(sequence => coordinator.PushAsync("player", Direction.Up, sequence))
            .ToArray();
        Assert.All(pending, task => Assert.False(task.IsCompleted));
        await Error("INPUT_QUEUE_FULL", () => coordinator.PushAsync("player", Direction.Up, 2048));
        await Error("INVALID_DIRECTION", () => coordinator.PushAsync("player", (Direction)42, 2048));
        await Error("INVALID_SEQUENCE", () => coordinator.PushAsync("player", Direction.Down, 0));
        coordinator.Tick();
        await Task.WhenAll(pending);
        AdvanceToSnapshot(coordinator);

        var snapshot = await coordinator.Subscribe("player").ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1024, Assert.Single(snapshot.Players).LastProcessedSequence);
        Assert.Equal(0, snapshot.Arena.LeftPaddle.Velocity);
        Assert.Equal(0, snapshot.Arena.RightPaddle.Velocity);
    }

    [Fact]
    public async Task RateHistorySurvivesResumeAndAgesAtOneSecond()
    {
        using var coordinator = NewCoordinator();
        coordinator.Admit("player");
        coordinator.Admit("opponent");
        var joined = await Complete(coordinator, coordinator.JoinAsync("player", "Player", null));
        await Complete(coordinator, coordinator.JoinAsync("opponent", "Opponent", null));
        var accepted = Enumerable.Range(1, 10)
            .Select(sequence => coordinator.PushAsync("player", Direction.Up, sequence)).ToArray();
        coordinator.Tick();
        await Task.WhenAll(accepted);
        var played = await ReadSnapshot(coordinator, "player");
        var previous = played.Players.Single(player => player.PlayerId == joined.PlayerId);
        Assert.Equal(10, previous.RecentPushCount);
        Assert.NotNull(previous.LastUpTick);

        await Complete(coordinator, coordinator.DisconnectAsync("player"));
        coordinator.Admit("resumed");
        var resumed = await Complete(coordinator, coordinator.JoinAsync("resumed", "Ignored", joined.ResumeToken));
        Assert.Equal(10, resumed.LastProcessedSequence);
        var restored = resumed.Snapshot.Players.Single(player => player.PlayerId == joined.PlayerId);
        Assert.Equal(0, restored.RecentPushCount);
        Assert.Null(restored.LastUpTick);
        Assert.Null(restored.LastDownTick);

        time.Advance(TimeSpan.FromMilliseconds(999));
        await Complete(coordinator, coordinator.PushAsync("resumed", Direction.Up, 11));
        Assert.Equal(0, Paddle(joined.Team).Velocity);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await Complete(coordinator, coordinator.PushAsync("resumed", Direction.Down, 12));
        Assert.True(Paddle(joined.Team).Velocity > 0);
        var snapshot = await ReadSnapshot(coordinator, "resumed");
        var player = snapshot.Players.Single(player => player.PlayerId == joined.PlayerId);
        Assert.Equal(12, player.LastProcessedSequence);
        Assert.Equal(1, player.RecentPushCount);
        Assert.Null(player.LastUpTick);
        Assert.NotNull(player.LastDownTick);
    }

    [Fact]
    public async Task SessionRetentionAdmissionAndTombstonesAreBounded()
    {
        using var coordinator = NewCoordinator();
        var tokens = new List<string>();
        for (var index = 0; index < 129; index++)
        {
            coordinator.Admit("cycling");
            var joined = await Complete(coordinator, coordinator.JoinAsync("cycling", "Player", null));
            tokens.Add(joined.ResumeToken);
            await Complete(coordinator, coordinator.DisconnectAsync("cycling"));
        }

        coordinator.Admit("probe");
        await Error("INVALID_RESUME_TOKEN", () =>
            Complete(coordinator, coordinator.JoinAsync("probe", "Player", tokens[0])));
        await Error("EXPIRED_RESUME_TOKEN", () =>
            Complete(coordinator, coordinator.JoinAsync("probe", "Player", tokens[1])));
        var resumed = await Complete(coordinator, coordinator.JoinAsync("probe", "Ignored", tokens[65]));
        Assert.Equal(tokens[65], resumed.ResumeToken);

        for (var index = 0; index < 63; index++)
            coordinator.Admit(index.ToString());

        await Error("CAPACITY_REACHED", () => Task.FromResult(coordinator.Admit("overflow")));
        await Complete(coordinator, coordinator.DisconnectAsync("0"));
        coordinator.Admit("overflow");
    }

    [Fact]
    public async Task SlowSenderTimesOutWhileAnotherSubscriberReceivesLatestSnapshot()
    {
        using var coordinator = NewCoordinator();
        coordinator.Admit("slow");
        coordinator.Admit("fast");
        await Complete(coordinator, coordinator.JoinAsync("slow", "Slow", null));
        await Complete(coordinator, coordinator.JoinAsync("fast", "Fast", null));
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aborted = false;
        var slowReader = coordinator.Subscribe("slow");
        var slowSender = SnapshotSender.RunAsync(slowReader, (_, _) =>
        {
            sending.SetResult();
            return stalled.Task;
        }, () => aborted = true, time, CancellationToken.None);

        AdvanceToSnapshot(coordinator);
        await sending.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var fastReader = coordinator.Subscribe("fast");
        var first = await fastReader.ReadAsync(TestContext.Current.CancellationToken);
        AdvanceToSnapshot(coordinator);
        AdvanceToSnapshot(coordinator);
        var latest = await fastReader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first.Arena.Tick + 6, latest.Arena.Tick);
        Assert.False(fastReader.TryRead(out _));
        Assert.True(slowReader.TryPeek(out var buffered));
        Assert.Equal(latest.Arena.Tick, buffered.Arena.Tick);

        time.Advance(TimeSpan.FromSeconds(5));
        await slowSender.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(aborted);
        stalled.SetResult();
    }

    [Fact]
    public async Task LifecycleBackpressureAwaitsAndHostShutdownReleasesWaiters()
    {
        using var coordinator = NewCoordinator();
        await coordinator.StartAsync(CancellationToken.None);
        coordinator.Admit("player");
        var pending = Enumerable.Range(0, 129)
            .Select(_ => coordinator.JoinAsync("player", "Player", null)).ToArray();
        Assert.All(pending, task => Assert.False(task.IsCompleted));

        await coordinator.StopAsync(CancellationToken.None);
        foreach (var task in pending)
            await Error("SERVER_STOPPING", () => task);
        await Error("SERVER_STOPPING", () => Task.FromResult(coordinator.Admit("after-shutdown")));
    }

    [Fact]
    public async Task ClockDiscardsLagAfterFiveTicks()
    {
        var clock = new ObservedClock();
        using var coordinator = new GameCoordinator(engine, clock, new Random(2));
        await coordinator.StartAsync(CancellationToken.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await clock.TimerArmed.WaitAsync(deadline.Token);

        clock.Inner.Advance(TimeSpan.FromSeconds(2));
        await clock.NextTimerArmed.WaitAsync(deadline.Token);
        Assert.Equal(5, engine.State.Tick);

        clock.Inner.Advance(TimeSpan.FromMilliseconds(17));
        await clock.ThirdTimerArmed.WaitAsync(deadline.Token);
        Assert.Equal(6, engine.State.Tick);
        await coordinator.StopAsync(deadline.Token);
    }

    [Fact]
    public async Task ParticipationOrdersTeamByCountThenOrdinalNameAndIdAndExpiresWithoutInput()
    {
        using var coordinator = new GameCoordinator(engine, time, new FirstTeamRandom());
        var joins = new List<JoinResult>();
        foreach (var name in new[] { "z", "RightA", "A", "RightB", "a", "RightC", "A" })
        {
            var connection = joins.Count.ToString();
            coordinator.Admit(connection);
            joins.Add(await Complete(coordinator, coordinator.JoinAsync(connection, name, null)));
        }

        var initial = await ReadSnapshot(coordinator, "0");
        var sameName = new[] { joins[2].PlayerId, joins[6].PlayerId }.Order().ToArray();
        Assert.Equal(new[] { sameName[0], sameName[1], joins[4].PlayerId, joins[0].PlayerId },
            initial.Players.Where(player => player.Team == Team.Left).Select(player => player.PlayerId));
        Assert.Equal(new[] { Team.Left, Team.Left, Team.Left, Team.Left, Team.Right, Team.Right, Team.Right },
            initial.Players.Select(player => player.Team));

        var upTick = engine.State.Tick + 1;
        await Complete(coordinator, coordinator.PushAsync("0", Direction.Up, 1));
        time.Advance(TimeSpan.FromSeconds(1));
        var downTick = engine.State.Tick + 1;
        await Complete(coordinator, coordinator.PushAsync("0", Direction.Down, 2));
        await Complete(coordinator, coordinator.PushAsync("0", Direction.Up, 2));
        var active = await ReadSnapshot(coordinator, "0");
        var leader = active.Players[0];
        Assert.Equal(joins[0].PlayerId, leader.PlayerId);
        Assert.Equal(2, leader.RecentPushCount);
        Assert.Equal(upTick, leader.LastUpTick);
        Assert.Equal(downTick, leader.LastDownTick);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, (await ReadSnapshot(coordinator, "0")).Players[0].RecentPushCount);
        time.Advance(TimeSpan.FromSeconds(1));
        var idle = await ReadSnapshot(coordinator, "0");
        Assert.All(idle.Players, player => Assert.Equal(0, player.RecentPushCount));
        Assert.Equal(joins[0].PlayerId, idle.Players[3].PlayerId);
    }

    private async Task<GameSnapshot> ReadSnapshot(GameCoordinator coordinator, string connectionId)
    {
        AdvanceToSnapshot(coordinator);
        return await coordinator.Subscribe(connectionId).ReadAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FirstTeamRandom : Random
    {
        public override int Next(int maxValue) => 0;
    }

    private PaddleState Paddle(Team team) => team == Team.Left ? engine.State.LeftPaddle : engine.State.RightPaddle;

    private void AdvanceToSnapshot(GameCoordinator coordinator)
    {
        do
            coordinator.Tick();
        while (engine.State.Tick % 3 != 0);
    }

    private static Task Complete(GameCoordinator coordinator, Task operation)
    {
        coordinator.Tick();
        return operation;
    }

    private static Task<T> Complete<T>(GameCoordinator coordinator, Task<T> operation)
    {
        coordinator.Tick();
        return operation;
    }

    private static async Task Error(string marker, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<HubException>(action);
        Assert.Equal($"PONGSOLE_{marker}", exception.Message);
    }

    private sealed class ObservedClock : TimeProvider
    {
        private readonly TaskCompletionSource[] armed = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        private int count;

        internal FakeTimeProvider Inner { get; } = new();
        internal Task TimerArmed => armed[0].Task;
        internal Task NextTimerArmed => armed[1].Task;
        internal Task ThirdTimerArmed => armed[2].Task;
        public override long GetTimestamp() => Inner.GetTimestamp();
        public override long TimestampFrequency => Inner.TimestampFrequency;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Inner.CreateTimer(callback, state, dueTime, period);
            var index = Interlocked.Increment(ref count) - 1;
            if (index < armed.Length)
                armed[index].SetResult();
            return timer;
        }
    }
}
