using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Threading.Channels;
using Pongsole.Contracts;
using Pongsole.Core;

namespace Pongsole.Server;

internal sealed class GameCoordinator(GameEngine engine, TimeProvider time, Random teamRandom) : BackgroundService, IGameCoordinator
{
    private const int ConnectionLimit = 64;
    private const int RetainedLimit = 64;
    private const int LifecycleCapacity = 128;
    private const int InputCapacity = 1024;
    private const int CatchUpLimit = 5;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1d / 60);
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ParticipationWindow = TimeSpan.FromSeconds(10);

    private readonly Lock gate = new();
    private readonly Dictionary<string, ConnectionLease> connections = [];
    private readonly Dictionary<string, Session> sessions = [];
    private readonly Queue<Session> retained = new(RetainedLimit);
    private readonly Queue<string> tombstoneOrder = new(RetainedLimit);
    private readonly HashSet<string> tombstones = [];
    private readonly Channel<LifecycleCommand> lifecycle = Channel.CreateBounded<LifecycleCommand>(LifecycleCapacity);
    private readonly Channel<InputCommand> input = Channel.CreateBounded<InputCommand>(InputCapacity);
    private int leftCount;
    private int rightCount;
    private bool stopping;

    internal ConnectionLease Admit(string connectionId)
    {
        lock (gate)
        {
            if (stopping)
                throw GameErrors.ServerStopping();
            if (connections.ContainsKey(connectionId))
                throw GameErrors.AlreadyJoined();
            if (connections.Count >= ConnectionLimit)
                throw GameErrors.CapacityReached();

            var lease = new ConnectionLease(connectionId);
            connections.Add(connectionId, lease);
            return lease;
        }
    }

    internal void UpdateLatency(ConnectionLease lease, int? milliseconds, long measuredAt)
    {
        lock (gate)
        {
            if (!stopping && IsCurrent(lease))
            {
                lease.LatencyMilliseconds = milliseconds;
                lease.LatencyMeasuredAt = measuredAt;
            }
        }
    }

    public Task<JoinResult> JoinAsync(string connectionId, string displayName, string? resumeToken)
    {
        lock (gate)
        {
            var lease = GetLease(connectionId);
            return JoinAsync(lease, displayName, resumeToken);
        }
    }

    internal Task<JoinResult> JoinAsync(ConnectionLease lease, string displayName, string? resumeToken)
    {
        var completion = NewCompletion<JoinResult>();
        return EnqueueLifecycleAsync(new JoinCommand(lease, displayName, resumeToken, completion), completion.Task);
    }

    public Task PushAsync(string connectionId, Direction direction, long sequence)
    {
        lock (gate)
        {
            return PushAsync(GetLease(connectionId), direction, sequence);
        }
    }

    internal Task PushAsync(ConnectionLease lease, Direction direction, long sequence)
    {
        lock (gate)
        {
            if (stopping)
                throw GameErrors.ServerStopping();
            if (direction is not (Direction.Up or Direction.Down))
                throw GameErrors.InvalidDirection();
            if (sequence <= 0)
                throw GameErrors.InvalidSequence();
            if (!IsCurrent(lease) || lease.Session is not { } session)
                throw GameErrors.NotJoined();

            var completion = NewCompletion<bool>();
            if (!input.Writer.TryWrite(new InputCommand(lease, session, direction, sequence, completion)))
                throw GameErrors.InputQueueFull();
            return completion.Task;
        }
    }

    internal Task SetHeldAsync(ConnectionLease lease, Direction? direction, long sequence)
    {
        lock (gate)
        {
            if (stopping)
                throw GameErrors.ServerStopping();
            if (direction is not (null or Direction.Up or Direction.Down))
                throw GameErrors.InvalidDirection();
            if (sequence <= 0)
                throw GameErrors.InvalidSequence();
            if (!IsCurrent(lease) || lease.Session is not { } session)
                throw GameErrors.NotJoined();
            if (sequence <= session.LastProcessedSequence)
                return Task.CompletedTask;

            session.LastProcessedSequence = sequence;
            var held = lease.Held;
            if (direction is null)
            {
                held.Direction = null;
                return Task.CompletedTask;
            }
            if (engine.State.Phase == Phase.Waiting || leftCount == 0 || rightCount == 0)
                return Task.CompletedTask;

            var now = time.GetTimestamp();
            while (held.Updates.TryPeek(out var timestamp) && time.GetElapsedTime(timestamp, now) >= RateWindow)
                held.Updates.Dequeue();
            if (held.Updates.Count == 40)
                return Task.CompletedTask;

            held.Updates.Enqueue(now);
            held.RefreshedAt = now;
            if (held.Direction != direction)
            {
                if (held.Pending.Count < 10)
                    held.Pending.Enqueue(direction.Value);
                held.Direction = direction;
                held.NextRepeatTick = engine.State.Tick + 7;
            }
            return Task.CompletedTask;
        }
    }

    public Task DisconnectAsync(string connectionId)
    {
        lock (gate)
        {
            return connections.TryGetValue(connectionId, out var lease) ? DisconnectAsync(lease) : Task.CompletedTask;
        }
    }

    internal Task DisconnectAsync(ConnectionLease lease)
    {
        lock (gate)
        {
            if (stopping || !IsCurrent(lease))
                return Task.CompletedTask;

            var completion = NewCompletion<bool>();
            return EnqueueLifecycleAsync(new DisconnectCommand(lease, lease.Session, time.GetTimestamp(), completion), completion.Task);
        }
    }

    public ChannelReader<GameSnapshot> Subscribe(string connectionId)
    {
        lock (gate)
        {
            return GetLease(connectionId).Snapshots.Reader;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var origin = time.GetTimestamp();
            var next = TickInterval;
            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = next - time.GetElapsedTime(origin);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, time, stoppingToken);
                    continue;
                }

                var ticks = 0;
                do
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    Tick();
                    next += TickInterval;
                    ticks++;
                }
                while (ticks < CatchUpLimit && next <= time.GetElapsedTime(origin));

                if (next <= time.GetElapsedTime(origin))
                {
                    origin = time.GetTimestamp();
                    next = TickInterval;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            StopAcceptingCommands();
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopAcceptingCommands();
        return base.StopAsync(cancellationToken);
    }

    private void StopAcceptingCommands()
    {
        lock (gate)
        {
            if (stopping)
                return;

            stopping = true;
            lifecycle.Writer.TryComplete();
            input.Writer.TryComplete();
            while (lifecycle.Reader.TryRead(out var command))
                command.Stop();
            while (input.Reader.TryRead(out var push))
                push.Completion.TrySetException(GameErrors.ServerStopping());
            foreach (var lease in connections.Values)
            {
                lease.Held.Clear();
                lease.Snapshots.Writer.TryComplete();
            }
            connections.Clear();
        }
    }

    internal void Tick()
    {
        lock (gate)
        {
            if (stopping)
                return;

            var now = time.GetTimestamp();
            Expire(now);
            for (var count = 0; count < LifecycleCapacity && lifecycle.Reader.TryRead(out var command); count++)
                command.Apply(this, now);

            var leftPushes = 0;
            var rightPushes = 0;
            var canMove = engine.State.Phase != Phase.Waiting && leftCount > 0 && rightCount > 0;
            for (var count = 0; count < InputCapacity && input.Reader.TryRead(out var push); count++)
            {
                var impulse = ProcessPush(push, canMove, now);
                if (push.Session.Team == Team.Left)
                    leftPushes += impulse;
                else
                    rightPushes += impulse;
                push.Completion.TrySetResult(true);
            }

            foreach (var lease in connections.Values)
            {
                if (lease.Session is not { } session)
                    continue;

                var impulse = ProcessHeld(lease.Held, session, canMove, now);
                if (session.Team == Team.Left)
                    leftPushes += impulse;
                else
                    rightPushes += impulse;
            }

            var arena = engine.Advance(leftPushes, rightPushes, leftCount > 0, rightCount > 0);
            if (arena.Tick % 3 != 0)
                return;

            var snapshot = Snapshot(now);
            foreach (var lease in connections.Values)
            {
                if (lease.Session is not null)
                    lease.Snapshots.Writer.TryWrite(snapshot);
            }
        }
    }

    private int ProcessPush(InputCommand push, bool canMove, long now)
    {
        var session = push.Session;
        if (session.Lease != push.Lease || push.Sequence <= session.LastProcessedSequence)
            return 0;

        session.LastProcessedSequence = push.Sequence;
        return canMove ? ApplyImpulse(session, push.Direction, now) : 0;
    }

    private int ProcessHeld(HeldInput held, Session session, bool canMove, long now)
    {
        if (!canMove)
        {
            held.Clear();
            return 0;
        }
        if (held.Direction is not null && time.GetElapsedTime(held.RefreshedAt, now) >= RateWindow)
            held.Clear();

        var impulse = 0;
        var initial = held.Pending.Count > 0;
        while (held.Pending.TryDequeue(out var direction))
            impulse += ApplyImpulse(session, direction, now);

        var tick = engine.State.Tick + 1;
        if (!initial && held.Direction is { } repeating && tick >= held.NextRepeatTick)
        {
            impulse += ApplyImpulse(session, repeating, now);
            held.NextRepeatTick = tick + 6;
        }
        return impulse;
    }

    private int ApplyImpulse(Session session, Direction direction, long now)
    {
        while (session.AcceptedInputs.TryPeek(out var timestamp) &&
            time.GetElapsedTime(timestamp, now) >= RateWindow)
        {
            session.AcceptedInputs.Dequeue();
        }

        if (session.AcceptedInputs.Count >= 10)
            return 0;

        session.AcceptedInputs.Enqueue(now);
        PruneParticipation(session, now);
        session.RecentPushes.Enqueue(now);
        var appliedTick = engine.State.Tick + 1;
        if (direction == Direction.Up)
            session.LastUpTick = appliedTick;
        else
            session.LastDownTick = appliedTick;
        return direction == Direction.Up ? -1 : 1;
    }

    private JoinResult Join(ConnectionLease lease, string displayName, string? resumeToken, long now)
    {
        if (!IsCurrent(lease))
            throw GameErrors.NotJoined();
        if (lease.Session is not null)
            throw GameErrors.AlreadyJoined();

        Session session;
        if (resumeToken is not null)
        {
            if (!sessions.TryGetValue(resumeToken, out session!))
                throw tombstones.Contains(resumeToken) ? GameErrors.ExpiredResumeToken() : GameErrors.InvalidResumeToken();
            if (session.Lease is not null)
                throw GameErrors.SessionAlreadyConnected();

            RemoveRetained(session);
        }
        else
        {
            if (!PlayerName.IsValid(displayName))
                throw GameErrors.InvalidName();

            var team = leftCount == rightCount
                ? (Team)teamRandom.Next(2)
                : leftCount < rightCount ? Team.Left : Team.Right;
            session = new Session(Guid.NewGuid(), displayName, team, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            sessions.Add(session.Token, session);
        }

        lease.Session = session;
        session.Lease = lease;
        if (session.Team == Team.Left)
            leftCount++;
        else
            rightCount++;
        return new JoinResult(session.PlayerId, session.Team, session.Token, session.LastProcessedSequence, Snapshot(now));
    }

    private void Disconnect(ConnectionLease lease, Session? capturedSession, long requestedAt, long now)
    {
        if (!IsCurrent(lease) || capturedSession is not null && lease.Session != capturedSession)
            return;

        connections.Remove(lease.ConnectionId);
        lease.Held.Clear();
        lease.Snapshots.Writer.TryComplete();
        if (lease.Session is not { } session)
            return;

        lease.Session = null;
        session.Lease = null;
        session.RecentPushes.Clear();
        session.LastUpTick = null;
        session.LastDownTick = null;
        if (session.Team == Team.Left)
            leftCount--;
        else
            rightCount--;

        if (time.GetElapsedTime(requestedAt, now) >= ResumeWindow)
        {
            Forget(session);
            return;
        }

        session.DisconnectedAt = requestedAt;
        retained.Enqueue(session);
        if (retained.Count > RetainedLimit)
            Forget(retained.Dequeue());
    }

    private void Expire(long now)
    {
        var count = retained.Count;
        for (var index = 0; index < count; index++)
        {
            var session = retained.Dequeue();
            if (time.GetElapsedTime(session.DisconnectedAt, now) >= ResumeWindow)
                Forget(session);
            else
                retained.Enqueue(session);
        }
    }

    private void RemoveRetained(Session resumed)
    {
        var count = retained.Count;
        for (var index = 0; index < count; index++)
        {
            var session = retained.Dequeue();
            if (session != resumed)
                retained.Enqueue(session);
        }
    }

    private void Forget(Session session)
    {
        sessions.Remove(session.Token);
        tombstones.Add(session.Token);
        tombstoneOrder.Enqueue(session.Token);
        if (tombstoneOrder.Count > RetainedLimit)
            tombstones.Remove(tombstoneOrder.Dequeue());
    }

    private GameSnapshot Snapshot(long now) => new(engine.State, connections.Values
        .Where(lease => lease.Session is not null)
        .Select(lease => PlayerSnapshot(lease, now))
        .OrderBy(player => player.Team)
        .ThenByDescending(player => player.RecentPushCount)
        .ThenBy(player => player.DisplayName, StringComparer.Ordinal)
        .ThenBy(player => player.PlayerId)
        .ToImmutableArray());

    private PlayerState PlayerSnapshot(ConnectionLease lease, long now)
    {
        var session = lease.Session!;
        var latency = lease.LatencyMilliseconds is not null &&
            time.GetElapsedTime(lease.LatencyMeasuredAt, now) < TimeSpan.FromSeconds(6)
            ? lease.LatencyMilliseconds : null;
        PruneParticipation(session, now);
        return new PlayerState(session.PlayerId, session.DisplayName, session.Team, session.LastProcessedSequence,
            session.RecentPushes.Count, session.LastUpTick, session.LastDownTick, latency);
    }

    private void PruneParticipation(Session session, long now)
    {
        while (session.RecentPushes.TryPeek(out var timestamp) &&
            time.GetElapsedTime(timestamp, now) >= ParticipationWindow)
        {
            session.RecentPushes.Dequeue();
        }
    }

    private ConnectionLease GetLease(string connectionId)
    {
        if (stopping)
            throw GameErrors.ServerStopping();
        return connections.TryGetValue(connectionId, out var lease) ? lease : throw GameErrors.NotJoined();
    }

    private bool IsCurrent(ConnectionLease lease) =>
        connections.TryGetValue(lease.ConnectionId, out var current) && current == lease;

    private async Task<T> EnqueueLifecycleAsync<T>(LifecycleCommand command, Task<T> completion)
    {
        try
        {
            await lifecycle.Writer.WriteAsync(command);
        }
        catch (ChannelClosedException)
        {
            throw GameErrors.ServerStopping();
        }
        return await completion;
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class ConnectionLease(string connectionId)
    {
        internal string ConnectionId { get; } = connectionId;
        internal Session? Session { get; set; }
        internal HeldInput Held { get; } = new();
        internal int? LatencyMilliseconds { get; set; }
        internal long LatencyMeasuredAt { get; set; }
        internal Channel<GameSnapshot> Snapshots { get; } = Channel.CreateBounded<GameSnapshot>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    }

    internal sealed class HeldInput
    {
        internal Direction? Direction { get; set; }
        internal long RefreshedAt { get; set; }
        internal long NextRepeatTick { get; set; }
        internal Queue<Direction> Pending { get; } = new(10);
        internal Queue<long> Updates { get; } = new(40);

        internal void Clear()
        {
            Direction = null;
            Pending.Clear();
        }
    }

    internal sealed class Session(Guid playerId, string displayName, Team team, string token)
    {
        internal Guid PlayerId { get; } = playerId;
        internal string DisplayName { get; } = displayName;
        internal Team Team { get; } = team;
        internal string Token { get; } = token;
        internal ConnectionLease? Lease { get; set; }
        internal long LastProcessedSequence { get; set; }
        internal long DisconnectedAt { get; set; }
        internal Queue<long> AcceptedInputs { get; } = new(10);
        internal Queue<long> RecentPushes { get; } = new(100);
        internal long? LastUpTick { get; set; }
        internal long? LastDownTick { get; set; }
    }

    private abstract record LifecycleCommand
    {
        internal abstract void Apply(GameCoordinator owner, long now);
        internal abstract void Stop();
    }

    private sealed record JoinCommand(ConnectionLease Lease, string DisplayName, string? ResumeToken,
        TaskCompletionSource<JoinResult> Completion) : LifecycleCommand
    {
        internal override void Apply(GameCoordinator owner, long now)
        {
            try
            {
                Completion.TrySetResult(owner.Join(Lease, DisplayName, ResumeToken, now));
            }
            catch (Microsoft.AspNetCore.SignalR.HubException exception)
            {
                Completion.TrySetException(exception);
            }
        }

        internal override void Stop() => Completion.TrySetException(GameErrors.ServerStopping());
    }

    private sealed record DisconnectCommand(ConnectionLease Lease, Session? Session, long RequestedAt,
        TaskCompletionSource<bool> Completion) : LifecycleCommand
    {
        internal override void Apply(GameCoordinator owner, long now)
        {
            owner.Disconnect(Lease, Session, RequestedAt, now);
            Completion.TrySetResult(true);
        }

        internal override void Stop() => Completion.TrySetException(GameErrors.ServerStopping());
    }

    private sealed record InputCommand(ConnectionLease Lease, Session Session, Direction Direction, long Sequence,
        TaskCompletionSource<bool> Completion);
}
