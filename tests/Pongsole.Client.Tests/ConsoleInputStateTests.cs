using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;
using Pongsole.Transport;

namespace Pongsole.Client.Tests;

public sealed class ConsoleInputStateTests
{
    [Fact]
    public void HeartbeatRetainsPulseIdUntilAcknowledgedThenSendsNeutral()
    {
        var clock = new FakeTimeProvider();
        var wire = new Wire();
        var input = new ConsoleInputState(clock, wire, new PlayerSession());

        input.Update(true, Direction.Up, 0);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        input.Update(true, null, 0);
        input.Update(true, null, 1);

        Assert.Equal(new[]
        {
            (new InputState(Direction.Up, true, 1), 1L),
            (new InputState(Direction.Up, true, 1), 2L),
            (default(InputState), 3L)
        }, wire.Sent);
    }

    [Fact]
    public void BlockedOldPulseIsReplacedAndExpiresWithoutReplay()
    {
        var clock = new FakeTimeProvider();
        var wire = new Wire { Blocked = true };
        var input = new ConsoleInputState(clock, wire, new PlayerSession());

        input.Update(true, Direction.Up, 0);
        input.Update(true, Direction.Down, 0);
        wire.Blocked = false;
        input.Update(true, null, 0);
        Assert.Equal((new InputState(Direction.Down, true, 2), 2L), Assert.Single(wire.Sent));

        clock.Advance(TimeSpan.FromMilliseconds(100));
        input.Update(true, null, 0);
        Assert.Equal((default(InputState), 3L), wire.Sent[^1]);
    }

    [Fact]
    public void LeavingPlayReleasesImmediatelyEvenWhenPulseIsUnacknowledged()
    {
        var clock = new FakeTimeProvider();
        var wire = new Wire();
        var input = new ConsoleInputState(clock, wire, new PlayerSession());
        input.Update(true, Direction.Up, 0);
        input.Update(false, Direction.Down, 0);
        Assert.Equal((default(InputState), 2L), wire.Sent[^1]);
    }

    private sealed class Wire : IGameConnection
    {
        public List<(InputState, long)> Sent { get; } = [];
        public bool Blocked { get; set; }
        public bool IsConnected => true;
        public Task StartAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<JoinResult> JoinAsync(string name, string? token, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SelectModeAsync(GameMode mode, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public bool TrySend(InputState input, long sequence)
        {
            if (Blocked) return false;
            Sent.Add((input, sequence));
            return true;
        }
    }
}
