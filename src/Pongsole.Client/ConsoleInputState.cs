using Pongsole.Contracts;
using Pongsole.Transport;

namespace Pongsole.Client;

internal sealed class ConsoleInputState(TimeProvider clock, IGameConnection connection, PlayerSession player)
{
    private static readonly TimeSpan PulseLifetime = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(50);
    private TimeSpan? nextHeartbeat;
    private readonly long startedAt = clock.GetTimestamp();
    private long pressedAt;
    private long sentPulseSequence;
    private InputState current;
    private bool changed = true;

    public void Update(bool canMove, Direction? key, long lastProcessed)
    {
        var now = clock.GetTimestamp();
        if (canMove && key is { } direction)
        {
            current = new InputState(direction, true, player.NextPulse());
            pressedAt = now;
            sentPulseSequence = 0;
            changed = true;
        }
        else if (current.IsPulse && (!canMove || clock.GetElapsedTime(pressedAt, now) >= PulseLifetime
            || (sentPulseSequence > 0 && lastProcessed >= sentPulseSequence)))
        {
            current = default;
            changed = true;
        }

        var elapsed = clock.GetElapsedTime(startedAt, now);
        if (changed || nextHeartbeat is null || elapsed >= nextHeartbeat)
            Send(elapsed);
    }

    public void Release()
    {
        current = default;
        Send(clock.GetElapsedTime(startedAt));
    }

    private void Send(TimeSpan elapsed)
    {
        var sequence = Math.Max(player.NextSequence, current.PulseId);
        if (!connection.TrySend(current, sequence)) return;

        player.Reconcile(sequence);
        if (current.IsPulse && sentPulseSequence == 0) sentPulseSequence = sequence;
        nextHeartbeat = !changed && nextHeartbeat is { } due && elapsed - due < Heartbeat
            ? due + Heartbeat : elapsed + Heartbeat;
        changed = false;
    }
}
