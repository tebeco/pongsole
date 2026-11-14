using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;
using Pongsole.Core;
using Pongsole.Transport;

namespace Pongsole.Server.Tests;

public sealed class GameCoordinatorTests
{
    private readonly FakeTimeProvider time = new();
    private readonly GameEngine engine = new(1);
    private GameCoordinator NewCoordinator() => new(engine, time, new Random(2));

    [Fact]
    public async Task DiscordIdentitySuppliesTheNameAndOwnsItsResumeToken()
    {
        using var coordinator = NewCoordinator();
        const string username = "long.discord_username0123456789";
        var first = coordinator.Admit("first", 1, new DiscordPlayer("123", username));
        var joined = await Complete(coordinator, coordinator.JoinAsync(first, "Ignored", null));
        var payload = SnapshotCodec.Write(joined.Snapshot);
        Assert.True(SnapshotCodec.TryRead(payload, out var decoded));
        Assert.Equal(username, Assert.Single(decoded!.Players).DisplayName);
        await Complete(coordinator, coordinator.DisconnectAsync(first));

        var guest = coordinator.Admit("guest", 2);
        await Error("INVALID_RESUME_TOKEN", () => Complete(coordinator,
            coordinator.JoinAsync(guest, "", joined.ResumeToken)));

        var returning = coordinator.Admit("returning", 3, new DiscordPlayer("123", "renamed.user"));
        var resumed = await Complete(coordinator, coordinator.JoinAsync(returning, "Ignored", joined.ResumeToken));
        Assert.Equal(joined.PlayerId, resumed.PlayerId);
        Assert.Equal("renamed.user", Assert.Single(resumed.Snapshot.Players).DisplayName);
    }

    [Fact]
    public async Task ModeChoiceRequiresCurrentOldestPlayerAndFirstValidRequestLocksMode()
    {
        using var coordinator = NewCoordinator();
        var first = coordinator.Admit("first", 1);
        var second = coordinator.Admit("second", 2);
        var firstJoin = await Complete(coordinator, coordinator.JoinAsync(first, "First", null));
        var secondJoin = await Complete(coordinator, coordinator.JoinAsync(second, "Second", null));
        Assert.Equal(GameMode.AwaitingSelection, secondJoin.Snapshot.Arena.Mode);
        Assert.Equal(firstJoin.PlayerId, secondJoin.Snapshot.ModeSelectorPlayerId);
        coordinator.ReceiveInput(first, 1, 1, new InputState(Direction.Down, true, 1));
        var waiting = await ReadSnapshot(coordinator, "first");
        Assert.Equal(Phase.Waiting, waiting.Arena.Phase);
        Assert.All(waiting.Players, player => Assert.Equal(0, player.RecentPushCount));
        await Error("MODE_FORBIDDEN", () => Complete(coordinator,
            coordinator.SelectModeAsync(second, GameMode.IndividualPaddles)));
        await Error("INVALID_MODE", () => Complete(coordinator,
            coordinator.SelectModeAsync(first, GameMode.AwaitingSelection)));

        var selection = coordinator.SelectModeAsync(first, GameMode.IndividualPaddles);
        var race = coordinator.SelectModeAsync(first, GameMode.AllTogether);
        coordinator.Tick();
        Assert.Equal(GameMode.IndividualPaddles, await selection);
        await Error("MODE_LOCKED", () => race);
        var selected = await ReadSnapshot(coordinator, "first");
        Assert.Null(selected.ModeSelectorPlayerId);
        Assert.Equal(GameMode.IndividualPaddles, selected.Arena.Mode);
        Assert.Equal(2, selected.Arena.PlayerPaddles.Length);
        Assert.All(selected.Arena.PlayerPaddles, paddle => Assert.Equal(15, paddle.Y));
    }

    [Fact]
    public async Task SelectorTransfersAndSelectedModeSurvivesResumeUntilLastSessionExpires()
    {
        using var coordinator = NewCoordinator();
        var first = coordinator.Admit("first", 1);
        var second = coordinator.Admit("second", 2);
        await Complete(coordinator, coordinator.JoinAsync(first, "First", null));
        var secondJoin = await Complete(coordinator, coordinator.JoinAsync(second, "Second", null));
        var disconnect = coordinator.DisconnectAsync(first);
        var staleSelection = coordinator.SelectModeAsync(first, GameMode.AllTogether);
        coordinator.Tick();
        await disconnect;
        await Error("MODE_FORBIDDEN", () => staleSelection);
        Assert.Equal(secondJoin.PlayerId, (await ReadSnapshot(coordinator, "second")).ModeSelectorPlayerId);
        await Complete(coordinator, coordinator.SelectModeAsync(second, GameMode.IndividualPaddles));
        await Complete(coordinator, coordinator.DisconnectAsync(second));

        time.Advance(TimeSpan.FromSeconds(59));
        var returning = coordinator.Admit("returning", 3);
        var resumed = await Complete(coordinator, coordinator.JoinAsync(returning, "", secondJoin.ResumeToken));
        Assert.Equal(secondJoin.PlayerId, resumed.PlayerId);
        Assert.Equal(GameMode.IndividualPaddles, resumed.Snapshot.Arena.Mode);
        Assert.Equal(15, Assert.Single(resumed.Snapshot.Arena.PlayerPaddles).Y);
        await Complete(coordinator, coordinator.DisconnectAsync(returning));

        time.Advance(TimeSpan.FromSeconds(60));
        var next = coordinator.Admit("next", 4);
        var nextJoin = await Complete(coordinator, coordinator.JoinAsync(next, "Next", null));
        Assert.Equal(GameMode.AwaitingSelection, nextJoin.Snapshot.Arena.Mode);
        Assert.Equal(nextJoin.PlayerId, nextJoin.Snapshot.ModeSelectorPlayerId);
    }

    [Fact]
    public async Task PersonalMovementStaysWithItsPlayerAndResumedPaddleStartsCentered()
    {
        using var coordinator = NewCoordinator();
        var first = coordinator.Admit("first", 1);
        var second = coordinator.Admit("second", 2);
        var third = coordinator.Admit("third", 3);
        var firstJoin = await Complete(coordinator, coordinator.JoinAsync(first, "First", null));
        var secondJoin = await Complete(coordinator, coordinator.JoinAsync(second, "Second", null));
        var thirdJoin = await Complete(coordinator, coordinator.JoinAsync(third, "Third", null));
        await Complete(coordinator, coordinator.SelectModeAsync(first, GameMode.IndividualPaddles));

        coordinator.ReceiveInput(first, 1, 1, new InputState(Direction.Up, true, 1));
        var moved = await ReadSnapshot(coordinator, "first");
        Assert.True(moved.Arena.PlayerPaddles.Single(paddle => paddle.PlayerId == firstJoin.PlayerId).Y < 15);
        Assert.All(moved.Arena.PlayerPaddles.Where(paddle => paddle.PlayerId != firstJoin.PlayerId),
            paddle => Assert.Equal(15, paddle.Y));

        var teammate = thirdJoin.Team == firstJoin.Team ? thirdJoin : secondJoin;
        await Complete(coordinator, coordinator.DisconnectAsync(first));
        Assert.DoesNotContain(engine.State.PlayerPaddles, paddle => paddle.PlayerId == firstJoin.PlayerId);
        Assert.Contains(engine.State.PlayerPaddles, paddle => paddle.PlayerId == teammate.PlayerId);
        var replacement = coordinator.Admit("replacement", 4);
        var resumed = await Complete(coordinator, coordinator.JoinAsync(replacement, "", firstJoin.ResumeToken));
        Assert.Equal(15, resumed.Snapshot.Arena.PlayerPaddles.Single(paddle => paddle.PlayerId == firstJoin.PlayerId).Y);
        coordinator.ReceiveInput(first, 1, 3, new InputState(Direction.Up, true, 2));
        coordinator.ReceiveInput(replacement, 1, 3, new InputState(Direction.Up, true, 2));
        var next = await ReadSnapshot(coordinator, "replacement");
        Assert.Equal(15, next.Arena.PlayerPaddles.Single(paddle => paddle.PlayerId == firstJoin.PlayerId).Y);
    }

    [Fact]
    public async Task RealtimeNeutralOverwritesPendingInputAndOldPacketsCannotRestoreIt()
    {
        using var coordinator = NewCoordinator();
        var lease = coordinator.Admit("player", 10);
        coordinator.Admit("opponent");
        var join = await Complete(coordinator, coordinator.JoinAsync(lease, "Player", null));
        await Complete(coordinator, coordinator.SelectModeAsync(lease, GameMode.AllTogether));
        await Complete(coordinator, coordinator.JoinAsync("opponent", "Opponent", null));

        coordinator.ReceiveInput(lease, 10, 2, new InputState(Direction.Up, false, 0));
        coordinator.ReceiveInput(lease, 10, 4, new InputState(null, false, 0));
        coordinator.ReceiveInput(lease, 10, 3, new InputState(Direction.Down, false, 0));
        coordinator.ReceiveInput(lease, 10, 4, new InputState(Direction.Down, false, 0));
        coordinator.ReceiveInput(lease, 9, 5, new InputState(Direction.Down, false, 0));

        var snapshot = await ReadSnapshot(coordinator, "player");
        var player = snapshot.Players.Single(player => player.PlayerId == join.PlayerId);
        Assert.Equal(4, player.LastProcessedSequence);
        Assert.Equal(0, player.RecentPushCount);
    }

    [Fact]
    public async Task RealtimePulseDeduplicationAndHeldLeaseExpiryDoNotReplayMovement()
    {
        using var coordinator = NewCoordinator();
        var lease = coordinator.Admit("player", 10);
        coordinator.Admit("opponent");
        var join = await Complete(coordinator, coordinator.JoinAsync(lease, "Player", null));
        await Complete(coordinator, coordinator.SelectModeAsync(lease, GameMode.AllTogether));
        await Complete(coordinator, coordinator.JoinAsync("opponent", "Opponent", null));
        PlayerState Player(GameSnapshot state) => state.Players.Single(player => player.PlayerId == join.PlayerId);

        coordinator.ReceiveInput(lease, 10, 1, new InputState(Direction.Up, true, 1));
        coordinator.Tick();
        coordinator.ReceiveInput(lease, 10, 2, new InputState(Direction.Up, true, 1));
        var first = Player(await ReadSnapshot(coordinator, "player"));
        Assert.Equal(1, first.RecentPushCount);
        Assert.Equal(2, first.LastProcessedSequence);

        coordinator.ReceiveInput(lease, 10, 3, new InputState(Direction.Down, false, 0));
        var held = Player(await ReadSnapshot(coordinator, "player"));
        Assert.NotNull(held.LastDownTick);
        time.Advance(TimeSpan.FromSeconds(1));
        for (var tick = 0; tick < 10; tick++)
            coordinator.Tick();
        var expired = Player(await ReadSnapshot(coordinator, "player"));
        Assert.Equal(held.LastDownTick, expired.LastDownTick);
    }

    [Fact]
    public async Task RealtimeCapsImpulseRateAndAlwaysHonorsNeutral()
    {
        using var coordinator = NewCoordinator();
        var lease = coordinator.Admit("player", 10);
        coordinator.Admit("opponent");
        var join = await Complete(coordinator, coordinator.JoinAsync(lease, "Player", null));
        await Complete(coordinator, coordinator.SelectModeAsync(lease, GameMode.AllTogether));
        await Complete(coordinator, coordinator.JoinAsync("opponent", "Opponent", null));

        for (var sequence = 1; sequence <= 60; sequence++)
        {
            coordinator.ReceiveInput(lease, 10, sequence, new InputState(Direction.Up, true, sequence));
            coordinator.Tick();
        }

        coordinator.ReceiveInput(lease, 10, 61, new InputState(Direction.Down, false, 0));
        coordinator.ReceiveInput(lease, 10, 62, new InputState(null, false, 0));
        var player = (await ReadSnapshot(coordinator, "player")).Players.Single(player => player.PlayerId == join.PlayerId);
        Assert.Equal(62, player.LastProcessedSequence);
        Assert.Equal(10, player.RecentPushCount);
        Assert.Null(player.LastDownTick);
    }

    [Fact]
    public async Task RealtimeResumeRetainsProcessedSequenceAndRejectsPreviousLease()
    {
        using var coordinator = NewCoordinator();
        var old = coordinator.Admit("old", 10);
        coordinator.Admit("opponent");
        var join = await Complete(coordinator, coordinator.JoinAsync(old, "Player", null));
        await Complete(coordinator, coordinator.SelectModeAsync(old, GameMode.AllTogether));
        await Complete(coordinator, coordinator.JoinAsync("opponent", "Opponent", null));
        coordinator.ReceiveInput(old, 10, 4, new InputState(Direction.Up, false, 0));
        await Complete(coordinator, coordinator.DisconnectAsync(old));

        var current = coordinator.Admit("current", 11);
        var resumed = await Complete(coordinator, coordinator.JoinAsync(current, "", join.ResumeToken));
        Assert.Equal(join.PlayerId, resumed.PlayerId);
        Assert.Equal(4, resumed.LastProcessedSequence);

        coordinator.ReceiveInput(old, 10, 6, new InputState(Direction.Up, false, 0));
        coordinator.ReceiveInput(current, 10, 6, new InputState(Direction.Up, false, 0));
        coordinator.ReceiveInput(current, 11, 4, new InputState(Direction.Up, false, 0));
        coordinator.ReceiveInput(current, 11, 5, new InputState(Direction.Down, true, 1));
        var player = (await ReadSnapshot(coordinator, "current")).Players.Single(player => player.PlayerId == join.PlayerId);
        Assert.Equal(5, player.LastProcessedSequence);
        Assert.Null(player.LastUpTick);
        Assert.NotNull(player.LastDownTick);
    }

    [Fact]
    public async Task HeldAndDiscreteInputsShareTheImpulseLimitAndOppositesCancel()
    {
        using var coordinator = NewCoordinator();
        var first = coordinator.Admit("first");
        var second = coordinator.Admit("second");
        var third = coordinator.Admit("third");
        var one = await Complete(coordinator, coordinator.JoinAsync(first, "First", null));
        await Complete(coordinator, coordinator.SelectModeAsync(first, GameMode.AllTogether));
        await Complete(coordinator, coordinator.JoinAsync(second, "Second", null));
        var three = await Complete(coordinator, coordinator.JoinAsync(third, "Third", null));
        var teammate = one.Team == three.Team ? first : second;
        var teammateId = teammate == first ? one.PlayerId :
            three.Snapshot.Players.Single(player => player.DisplayName == "Second").PlayerId;

        await coordinator.SetHeldAsync(teammate, Direction.Up, 1);
        await coordinator.SetHeldAsync(third, Direction.Down, 1);
        for (var tick = 0; tick < 49; tick++)
        {
            time.Advance(TimeSpan.FromMilliseconds(16));
            coordinator.Tick();
        }
        var beforeDiscrete = await ReadSnapshot(coordinator, "third");
        var centered = three.Team == Team.Left ? beforeDiscrete.Arena.LeftPaddle : beforeDiscrete.Arena.RightPaddle;
        Assert.Equal(new PaddleState(15, 0), centered);
        var discrete = Enumerable.Range(2, 10)
            .Select(sequence => coordinator.PushAsync(third, Direction.Down, sequence)).ToArray();
        coordinator.Tick();
        await Task.WhenAll(discrete);
        var observed = await ReadSnapshot(coordinator, "third");
        var paddle = three.Team == Team.Left ? observed.Arena.LeftPaddle : observed.Arena.RightPaddle;
        Assert.Equal(9, observed.Players.Single(player => player.PlayerId == teammateId).RecentPushCount);
        Assert.Equal(10, observed.Players.Single(player => player.PlayerId == three.PlayerId).RecentPushCount);
        Assert.Equal(11, observed.Players.Single(player => player.PlayerId == three.PlayerId).LastProcessedSequence);
        Assert.InRange(paddle.Y, 15.12, 15.36);
    }


    [Fact]
    public async Task HeldStateRepeatsWithoutHeartbeatImpulsesAndExpires()
    {
        using var coordinator = NewCoordinator();
        var lease = coordinator.Admit("player");
        coordinator.Admit("opponent");
        var join = await Complete(coordinator, coordinator.JoinAsync(lease, "Player", null));
        await Complete(coordinator, coordinator.SelectModeAsync(lease, GameMode.AllTogether));
        await Complete(coordinator, coordinator.JoinAsync("opponent", "Opponent", null));

        await coordinator.SetHeldAsync(lease, Direction.Up, 1);
        for (var tick = 0; tick < 60; tick++)
        {
            time.Advance(TimeSpan.FromMilliseconds(16));
            coordinator.Tick();
        }
        var snapshot = await ReadSnapshot(coordinator, "player");
        PlayerState Player(GameSnapshot state) => state.Players.Single(candidate => candidate.PlayerId == join.PlayerId);
        Assert.Equal(10, Player(snapshot).RecentPushCount);
        Assert.NotNull(Player(snapshot).LastUpTick);

        time.Advance(TimeSpan.FromSeconds(1));
        await coordinator.SetHeldAsync(lease, Direction.Up, 1);
        var expired = await ReadSnapshot(coordinator, "player");
        Assert.Equal(Player(snapshot).LastUpTick, Player(expired).LastUpTick);
        Assert.Equal(1, Player(expired).LastProcessedSequence);
    }

    [Fact]
    public async Task HeldTapSurvivesReleaseWithinTickAndNeutralBypassesUpdateCap()
    {
        using var coordinator = NewCoordinator();
        var lease = coordinator.Admit("player");
        coordinator.Admit("opponent");
        var join = await Complete(coordinator, coordinator.JoinAsync(lease, "Player", null));
        await Complete(coordinator, coordinator.SelectModeAsync(lease, GameMode.AllTogether));
        await Complete(coordinator, coordinator.JoinAsync("opponent", "Opponent", null));

        for (var sequence = 1; sequence <= 40; sequence++)
            await coordinator.SetHeldAsync(lease, Direction.Down, sequence);
        await coordinator.SetHeldAsync(lease, Direction.Up, 41);
        await coordinator.SetHeldAsync(lease, null, 42);
        for (var tick = 0; tick < 18; tick++)
            coordinator.Tick();

        var observed = await ReadSnapshot(coordinator, "player");
        var player = observed.Players.Single(candidate => candidate.PlayerId == join.PlayerId);
        Assert.Equal(42, player.LastProcessedSequence);
        Assert.Equal(1, player.RecentPushCount);
        Assert.NotNull(player.LastDownTick);
        Assert.Null(player.LastUpTick);
    }


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
        var lease = coordinator.Admit("player");
        coordinator.Admit("opponent");
        var joined = await Complete(coordinator, coordinator.JoinAsync(lease, "Player", null));
        await Complete(coordinator, coordinator.SelectModeAsync(lease, GameMode.AllTogether));
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
            var lease = coordinator.Admit(connection);
            joins.Add(await Complete(coordinator, coordinator.JoinAsync(lease, name, null)));
            if (joins.Count == 1)
                await Complete(coordinator, coordinator.SelectModeAsync(lease, GameMode.AllTogether));
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
