using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class PresentationEffects(TimeProvider clock)
{
    private static readonly TimeSpan ScoreDuration = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan BallDuration = TimeSpan.FromMilliseconds(250);
    private readonly Dictionary<long, long?> balls = [];
    private long? leftSince;
    private long? rightSince;
    private ArenaState? previous;

    public void Observe(ArenaState? arena)
    {
        if (arena is null)
        {
            previous = null;
            balls.Clear();
            leftSince = rightSince = null;
            return;
        }

        var reset = previous is null || arena.Tick < previous.Tick || arena.Mode != previous.Mode || arena.LeftScore < previous.LeftScore
            || arena.RightScore < previous.RightScore || arena.Phase == Phase.Waiting;
        var now = clock.GetTimestamp();
        if (reset)
        {
            balls.Clear();
            leftSince = rightSince = null;
        }
        else if (previous is { } old)
        {
            if (arena.LeftScore > old.LeftScore) leftSince = now;
            if (arena.RightScore > old.RightScore) rightSince = now;
        }

        foreach (var id in balls.Keys.Where(id => !arena.Balls.Any(ball => ball.Id == id)).ToArray())
            balls.Remove(id);
        foreach (var ball in arena.Balls)
            balls.TryAdd(ball.Id, reset ? null : now);
        previous = arena;
    }

    public bool Score(Team team) => Active(team == Team.Left ? leftSince : rightSince, ScoreDuration);
    public bool Ball(long id) => balls.TryGetValue(id, out var since) && Active(since, BallDuration);

    private bool Active(long? since, TimeSpan duration) =>
        since is { } started && clock.GetElapsedTime(started) < duration;
}
