using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class RosterActivity(TimeProvider clock)
{
    private readonly Dictionary<Guid, PlayerActivity> activity = [];

    public void Accept(GameSnapshot snapshot, long timestamp, bool seed)
    {
        var present = snapshot.Players.Select(player => player.PlayerId).ToHashSet();
        foreach (var absent in activity.Keys.Where(id => !present.Contains(id)).ToArray())
        {
            activity.Remove(absent);
        }

        foreach (var player in snapshot.Players)
        {
            activity.TryGetValue(player.PlayerId, out var previous);
            activity[player.PlayerId] = new PlayerActivity(
                Observe(previous.Up, player.LastUpTick, snapshot.Arena.Tick, timestamp, seed),
                Observe(previous.Down, player.LastDownTick, snapshot.Arena.Tick, timestamp, seed));
        }
    }

    public RosterPlayer Read(PlayerState player, long timestamp, bool showActivity)
    {
        activity.TryGetValue(player.PlayerId, out var pulses);
        return new RosterPlayer(player.DisplayName, player.Team,
            showActivity && IsLit(pulses.Up, timestamp), showActivity && IsLit(pulses.Down, timestamp),
            player.LatencyMilliseconds);
    }

    private bool IsLit(Pulse pulse, long timestamp) =>
        pulse.Remaining > TimeSpan.Zero && clock.GetElapsedTime(pulse.StartedAt, timestamp) < pulse.Remaining;

    private static Pulse Observe(Pulse previous, long? eventTick, long snapshotTick, long timestamp, bool seed)
    {
        if (seed || eventTick is null)
        {
            return new Pulse(eventTick, timestamp, TimeSpan.Zero);
        }

        if (eventTick <= previous.Tick)
        {
            return previous;
        }

        var elapsedTicks = Math.Clamp(snapshotTick - eventTick.Value, 0, 12);
        var remaining = TimeSpan.FromMilliseconds((12 - elapsedTicks) * 1000.0 / 60);
        return new Pulse(eventTick, timestamp, remaining);
    }

    private readonly record struct PlayerActivity(Pulse Up, Pulse Down);
    private readonly record struct Pulse(long? Tick, long StartedAt, TimeSpan Remaining);
}
