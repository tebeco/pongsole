using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;

namespace Pongsole.Transport.Tests;

public sealed class RealtimeCodecTests
{
    private static string Vector(string name)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "realtime-v1.json")));
        return fixture.RootElement.GetProperty(name).GetString()!;
    }

    [Fact]
    public void InputMatchesSharedGoldenPacket()
    {
        Span<byte> body = stackalloc byte[InputState.PayloadSize];
        new InputState(Direction.Down, false, 0).Write(body);
        Span<byte> packet = stackalloc byte[RealtimePacket.MaxPacketSize];
        var size = RealtimePacket.Write(packet, RealtimeKind.Input, 7, 8, body);
        Assert.Equal(Vector("inputHex").ToUpperInvariant(),
            Convert.ToHexString(packet[..size]));
        Assert.True(RealtimePacket.TryRead(packet[..size], out var header));
        Assert.Equal(8, header.MessageId);
        Assert.True(InputState.TryRead(packet[RealtimePacket.HeaderSize..size], out var state));
        Assert.Equal(Direction.Down, state.Direction);
        Assert.False(RealtimePacket.TryRead(packet[..(size + 1)], out _));
    }

    [Fact]
    public void SnapshotRoundTripsMaximumCountsAndRejectsMalformedData()
    {
        var snapshot = new GameSnapshot(new ArenaState(42, 7, 9, Phase.Playing, 0,
            new PaddleState(10, -1), new PaddleState(15, 2),
            [.. Enumerable.Range(1, 10).Select(id => new BallState(id, 40, 10, -12, 24))],
            [.. Enumerable.Range(1, 12).Select(id => new PickupState(id, 20, 25, 10, 3600))]),
            [.. Enumerable.Range(1, 64).Select(id => new PlayerState(new Guid(id, 0, 0, new byte[8]),
                $"Player{id:0000}", Team.Right, id, 100, 40, null, 2000))]);

        var payload = SnapshotCodec.Write(snapshot);
        Assert.Equal(Vector("snapshotHex").ToUpperInvariant(), Convert.ToHexString(payload));
        Assert.InRange(payload.Length, 1000, RealtimePacket.MaxPayloadSize);
        Assert.True(SnapshotCodec.TryRead(payload, out var decoded));
        Assert.NotNull(decoded);
        Assert.Equal(payload, SnapshotCodec.Write(decoded));
        Assert.False(SnapshotCodec.TryRead([.. payload, 0], out _));
        Assert.False(SnapshotCodec.TryRead(payload.AsSpan(0, payload.Length - 1), out _));
        payload[24] = 99;
        Assert.False(SnapshotCodec.TryRead(payload, out _));
    }

    [Fact]
    public void LatestSnapshotEvictsOlderPartialAndExpiryDoesNotReopenSameTick()
    {
        var time = new FakeTimeProvider();
        var reader = new SnapshotReassembly(7, time);
        var body = new byte[2000];
        body[^1] = 33;
        byte[] Packet(long id, int chunk, long epoch = 7)
        {
            var packet = new byte[RealtimePacket.MaxPacketSize];
            var size = RealtimePacket.Write(packet, RealtimeKind.Snapshot, epoch, id, body, chunk);
            return packet[..size];
        }

        Assert.Null(reader.Accept(Packet(1, 0)));
        Assert.Null(reader.Accept(Packet(2, 2, 99)));
        Assert.Null(reader.Accept(Packet(2, 2)));
        Assert.Null(reader.Accept(Packet(1, 1)));
        Assert.Null(reader.Accept(Packet(2, 2)));
        Assert.Null(reader.Accept(Packet(2, 0)));
        Assert.Equal(body, reader.Accept(Packet(2, 1)));
        Assert.Null(reader.Accept(Packet(2, 1)));
        Assert.Null(reader.Accept(Packet(3, 0)));
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Null(reader.Accept(Packet(3, 1)));
        Assert.Null(reader.Accept(Packet(3, 2)));
        Assert.Null(reader.Accept(Packet(4, 0)));
        var conflict = Packet(4, 0);
        conflict[^1] = 7;
        Assert.Null(reader.Accept(conflict));
        Assert.Null(reader.Accept(Packet(4, 1)));
        Assert.Null(reader.Accept(Packet(4, 2)));
    }
}
