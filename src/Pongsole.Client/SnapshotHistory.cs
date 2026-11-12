using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class SnapshotHistory(TimeProvider clock)
{
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(50);
    private readonly object gate = new();
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
            AcceptJoinedSnapshot(join.Snapshot);
            if (earlySnapshot is not null)
            {
                AcceptJoinedSnapshot(earlySnapshot);
                earlySnapshot = null;
            }
        }
    }

    private void AcceptJoinedSnapshot(GameSnapshot snapshot)
    {
        if (newest is not null && snapshot.Arena.Tick < newest.Arena.Tick)
        {
            return;
        }

        if (newest?.Arena.Tick != snapshot.Arena.Tick)
        {
            previousArena = newest?.Arena;
            newestTimestamp = clock.GetTimestamp();
        }

        newest = snapshot;
        foreach (var player in snapshot.Players)
        {
            if (player.PlayerId == playerId)
            {
                lastProcessed = Math.Max(lastProcessed, player.LastProcessedSequence);
                break;
            }
        }
    }

    public (ArenaState? Arena, int PlayerCount, long LastProcessed) Read()
    {
        lock (gate)
        {
            if (newest is null)
            {
                return (null, 0, lastProcessed);
            }

            return (Interpolate(newest.Arena), newest.Players.Length, lastProcessed);
        }
    }

    private ArenaState Interpolate(ArenaState arena)
    {
        if (previousArena is not { } previous || arena.Phase == Phase.Waiting || arena.Phase != previous.Phase
            || arena.LeftScore != previous.LeftScore || arena.RightScore != previous.RightScore)
        {
            return arena;
        }

        var fraction = Math.Clamp(clock.GetElapsedTime(newestTimestamp) / SnapshotInterval, 0, 1);
        return arena with
        {
            LeftPaddle = arena.LeftPaddle with { Y = Lerp(previous.LeftPaddle.Y, arena.LeftPaddle.Y, fraction) },
            RightPaddle = arena.RightPaddle with { Y = Lerp(previous.RightPaddle.Y, arena.RightPaddle.Y, fraction) },
            Ball = arena.Ball with
            {
                X = Lerp(previous.Ball.X, arena.Ball.X, fraction),
                Y = Lerp(previous.Ball.Y, arena.Ball.Y, fraction)
            }
        };
    }

    private static double Lerp(double from, double to, double fraction) => from + (to - from) * fraction;
}
