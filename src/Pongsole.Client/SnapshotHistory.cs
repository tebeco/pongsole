using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class SnapshotHistory(TimeProvider clock)
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(50);
    private readonly object gate = new();
    private readonly RosterActivity activity = new(clock);
    private readonly BallPrediction prediction = new(clock);
    private GameSnapshot? earlySnapshot;
    private GameSnapshot? newest;
    private ArenaState? previousArena;
    private long newestTimestamp;
    private Guid playerId;
    private long lastProcessed;

    public void Accept(GameSnapshot snapshot)
    {
        lock (gate)
        {
            if (newest is not null)
            {
                AcceptJoinedSnapshot(snapshot);
            }
            else if (earlySnapshot is null || earlySnapshot.Arena.Tick <= snapshot.Arena.Tick)
            {
                earlySnapshot = snapshot;
            }
        }
    }

    public void EstablishBaseline(JoinResult join)
    {
        lock (gate)
        {
            playerId = join.PlayerId;
            lastProcessed = join.LastProcessedSequence;
            previousArena = null;
            newest = null;
            AcceptJoinedSnapshot(join.Snapshot, seedActivity: true);
            if (earlySnapshot is not null)
            {
                AcceptJoinedSnapshot(earlySnapshot, seedActivity: true);
                earlySnapshot = null;
            }
        }
    }

    private void AcceptJoinedSnapshot(GameSnapshot snapshot, bool seedActivity = false)
    {
        if (newest is not null && snapshot.Arena.Tick < newest.Arena.Tick)
        {
            return;
        }

        var now = clock.GetTimestamp();
        var modeChanged = newest?.Arena.Mode != snapshot.Arena.Mode;
        if (modeChanged || newest?.Arena.Tick != snapshot.Arena.Tick)
        {
            previousArena = newest?.Arena;
            newestTimestamp = now;
            var latency = snapshot.Players.FirstOrDefault(player => player.PlayerId == playerId)?.LatencyMilliseconds;
            prediction.Accept(snapshot.Arena, latency, now,
                seedActivity || modeChanged || previousArena?.Phase != snapshot.Arena.Phase);
        }

        activity.Accept(snapshot, now, seedActivity || modeChanged || snapshot.Arena.Phase == Phase.Waiting);
        newest = !modeChanged && newest?.Arena.Tick == snapshot.Arena.Tick ? snapshot with { Arena = newest.Arena } : snapshot;
        foreach (var player in snapshot.Players)
        {
            if (player.PlayerId == playerId)
            {
                lastProcessed = Math.Max(lastProcessed, player.LastProcessedSequence);
                break;
            }
        }
    }

    public long LastProcessed
    {
        get
        {
            lock (gate)
            {
                return lastProcessed;
            }
        }
    }

    public (ArenaState? Arena, RosterPlayer[] Players, long LastProcessed, Guid? Selector, string? SelectorName) Read(bool showActivity = true)
    {
        lock (gate)
        {
            if (newest is null)
            {
                return (null, [], lastProcessed, null, null);
            }

            var timestamp = clock.GetTimestamp();
            var players = newest.Players.Select(player => activity.Read(player, timestamp, showActivity)).ToArray();
            var selector = newest.ModeSelectorPlayerId;
            var selectorName = newest.Players.FirstOrDefault(player => player.PlayerId == selector)?.DisplayName;
            return (Present(newest.Arena, timestamp), players, lastProcessed, selector, selectorName);
        }
    }

    private ArenaState Present(ArenaState arena, long timestamp)
    {
        var balls = prediction.Read(arena, timestamp);
        var fraction = Math.Clamp(clock.GetElapsedTime(newestTimestamp, timestamp) / SnapshotInterval, 0, 1);
        if (previousArena is not { } previous || arena.Phase == Phase.Waiting || arena.Phase != previous.Phase || arena.Mode != previous.Mode
            || (arena.Mode != GameMode.IndividualPaddles
                && (arena.LeftScore != previous.LeftScore || arena.RightScore != previous.RightScore)))
        {
            return arena with { Balls = balls };
        }

        return arena with
        {
            LeftPaddle = arena.LeftPaddle with { Y = Lerp(previous.LeftPaddle.Y, arena.LeftPaddle.Y, fraction) },
            RightPaddle = arena.RightPaddle with { Y = Lerp(previous.RightPaddle.Y, arena.RightPaddle.Y, fraction) },
            Balls = balls,
            PlayerPaddles = PresentPaddles(arena, previous, fraction)
        };
    }

    private static System.Collections.Immutable.ImmutableArray<PlayerPaddleState> PresentPaddles(
        ArenaState arena, ArenaState previous, double fraction)
    {
        if (arena.PlayerPaddles.IsDefaultOrEmpty || previous.PlayerPaddles.IsDefaultOrEmpty)
            return arena.PlayerPaddles;

        var old = previous.PlayerPaddles.ToDictionary(paddle => paddle.PlayerId);
        return [.. arena.PlayerPaddles.Select(paddle => old.TryGetValue(paddle.PlayerId, out var prior)
            ? paddle with { Y = Lerp(prior.Y, paddle.Y, fraction) }
            : paddle)];
    }

    private static double Lerp(double from, double to, double fraction) => from + (to - from) * fraction;
}
