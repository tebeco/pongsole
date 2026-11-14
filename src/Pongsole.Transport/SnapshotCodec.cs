using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Pongsole.Contracts;

namespace Pongsole.Transport;

public static class SnapshotCodec
{
    public static byte[] Write(GameSnapshot snapshot)
    {
        var arena = snapshot.Arena;
        Check(arena.Balls.Length is >= 1 and <= 10 && arena.Pickups.Length <= 12 && snapshot.Players.Length <= 64);
        using var buffer = new MemoryStream(RealtimePacket.MaxPayloadSize);
        using var writer = new BinaryWriter(buffer);
        writer.Write(arena.Tick);
        writer.Write(arena.LeftScore);
        writer.Write(arena.RightScore);
        writer.Write((byte)arena.Phase);
        writer.Write(checked((ushort)arena.ServeTicksRemaining));
        writer.Write(arena.LeftPaddle.Y);
        writer.Write(arena.LeftPaddle.Velocity);
        writer.Write(arena.RightPaddle.Y);
        writer.Write(arena.RightPaddle.Velocity);
        writer.Write((byte)arena.Balls.Length);
        foreach (var ball in arena.Balls)
        {
            writer.Write(ball.Id);
            writer.Write(ball.X);
            writer.Write(ball.Y);
            writer.Write(ball.VelocityX);
            writer.Write(ball.VelocityY);
        }

        writer.Write((byte)arena.Pickups.Length);
        foreach (var pickup in arena.Pickups)
        {
            writer.Write(pickup.Id);
            writer.Write(pickup.X);
            writer.Write(pickup.Y);
            writer.Write(checked((byte)pickup.Amount));
            writer.Write(checked((ushort)pickup.RemainingTicks));
        }

        writer.Write((byte)snapshot.Players.Length);
        Span<byte> uuid = stackalloc byte[16];
        foreach (var player in snapshot.Players)
        {
            Check(PlayerName.IsValidDisplayName(player.DisplayName));
            player.PlayerId.TryWriteBytes(uuid, bigEndian: true, out _);
            writer.Write(uuid);
            writer.Write((byte)player.DisplayName.Length);
            writer.Write(Encoding.ASCII.GetBytes(player.DisplayName));
            writer.Write((byte)player.Team);
            writer.Write(player.LastProcessedSequence);
            writer.Write(checked((ushort)player.RecentPushCount));
            writer.Write(player.LastUpTick ?? -1);
            writer.Write(player.LastDownTick ?? -1);
            writer.Write(player.LatencyMilliseconds is { } latency ? checked((ushort)latency) : ushort.MaxValue);
        }

        var paddles = arena.PlayerPaddles.IsDefault ? ImmutableArray<PlayerPaddleState>.Empty : arena.PlayerPaddles;
        Check(ValidMode(arena.Mode, arena.Phase, snapshot.ModeSelectorPlayerId, snapshot.Players, paddles));
        writer.Write((byte)arena.Mode);
        writer.Write(snapshot.ModeSelectorPlayerId.HasValue);
        if (snapshot.ModeSelectorPlayerId is { } selector)
        {
            selector.TryWriteBytes(uuid, bigEndian: true, out _);
            writer.Write(uuid);
        }

        writer.Write((byte)paddles.Length);
        foreach (var paddle in paddles)
        {
            paddle.PlayerId.TryWriteBytes(uuid, bigEndian: true, out _);
            writer.Write(uuid);
            writer.Write((byte)paddle.Team);
            writer.Write(paddle.Y);
            writer.Write(paddle.Velocity);
        }

        return buffer.ToArray();
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out GameSnapshot? snapshot)
    {
        snapshot = null;
        if (payload.Length > RealtimePacket.MaxPayloadSize)
            return false;

        try
        {
            var reader = new Reader(payload);
            var tick = reader.SafeInteger();
            var leftScore = reader.SafeInteger();
            var rightScore = reader.SafeInteger();
            var phase = (Phase)reader.Byte();
            var remaining = reader.UInt16();
            Check(phase is Phase.Waiting or Phase.Serving or Phase.Playing && remaining <= 60);
            var left = new PaddleState(reader.Number(), reader.Number());
            var right = new PaddleState(reader.Number(), reader.Number());

            var ballCount = reader.Byte();
            Check(ballCount is >= 1 and <= 10);
            var balls = ImmutableArray.CreateBuilder<BallState>(ballCount);
            var ballIds = new HashSet<long>();
            for (var i = 0; i < ballCount; i++)
            {
                var id = reader.SafeInteger();
                Check(id > 0 && ballIds.Add(id));
                balls.Add(new BallState(id, reader.Number(), reader.Number(), reader.Number(), reader.Number()));
            }

            var pickupCount = reader.Byte();
            Check(pickupCount <= 12);
            var pickups = ImmutableArray.CreateBuilder<PickupState>(pickupCount);
            var pickupIds = new HashSet<long>();
            for (var i = 0; i < pickupCount; i++)
            {
                var id = reader.SafeInteger();
                Check(id > 0 && pickupIds.Add(id));
                var x = reader.Number();
                var y = reader.Number();
                var amount = reader.Byte();
                var ttl = reader.UInt16();
                Check(amount is >= 1 and <= 10 && ttl <= 3600);
                pickups.Add(new PickupState(id, x, y, amount, ttl));
            }

            var playerCount = reader.Byte();
            Check(playerCount <= 64);
            var players = ImmutableArray.CreateBuilder<PlayerState>(playerCount);
            var playerIds = new HashSet<Guid>();
            for (var i = 0; i < playerCount; i++)
            {
                var id = new Guid(reader.Take(16), bigEndian: true);
                var nameLength = reader.Byte();
                Check(id != Guid.Empty && playerIds.Add(id) && nameLength is >= 1 and <= PlayerName.MaximumDisplayLength);
                var name = Encoding.ASCII.GetString(reader.Take(nameLength));
                var team = (Team)reader.Byte();
                var sequence = reader.SafeInteger();
                var count = reader.UInt16();
                var upTick = reader.OptionalTick(tick);
                var downTick = reader.OptionalTick(tick);
                var latency = reader.UInt16();
                Check(PlayerName.IsValidDisplayName(name) && team is Team.Left or Team.Right && count <= 100 &&
                    (latency <= 2000 || latency == ushort.MaxValue));
                players.Add(new PlayerState(id, name, team, sequence, count, upTick, downTick,
                    latency == ushort.MaxValue ? null : latency));
            }

            var mode = (GameMode)reader.Byte();
            var selectorFlag = reader.Byte();
            Check(selectorFlag <= 1);
            Guid? selector = selectorFlag == 1 ? new Guid(reader.Take(16), bigEndian: true) : null;
            var paddleCount = reader.Byte();
            Check(paddleCount <= 64);
            var paddles = ImmutableArray.CreateBuilder<PlayerPaddleState>(paddleCount);
            for (var i = 0; i < paddleCount; i++)
            {
                paddles.Add(new PlayerPaddleState(new Guid(reader.Take(16), bigEndian: true),
                    (Team)reader.Byte(), reader.Number(), reader.Number()));
            }

            var roster = players.MoveToImmutable();
            var personalPaddles = paddles.MoveToImmutable();
            Check(reader.IsEmpty && ValidMode(mode, phase, selector, roster, personalPaddles));
            var arena = new ArenaState(tick, leftScore, rightScore, phase, remaining, left, right,
                balls.MoveToImmutable(), pickups.MoveToImmutable(), mode, personalPaddles);
            snapshot = new GameSnapshot(arena, roster, selector);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool ValidMode(GameMode mode, Phase phase, Guid? selector, ImmutableArray<PlayerState> players,
        ImmutableArray<PlayerPaddleState> paddles)
    {
        if (mode == GameMode.AwaitingSelection)
            return phase == Phase.Waiting && paddles.IsEmpty &&
                (players.IsEmpty ? selector is null : players.Any(player => player.PlayerId == selector));

        if (selector is not null)
            return false;
        if (mode == GameMode.AllTogether)
            return paddles.IsEmpty;
        if (mode != GameMode.IndividualPaddles || paddles.Length != players.Length)
            return false;

        var roster = new Dictionary<Guid, Team>(players.Length);
        foreach (var player in players)
        {
            if (!roster.TryAdd(player.PlayerId, player.Team))
                return false;
        }

        foreach (var paddle in paddles)
        {
            if (!roster.Remove(paddle.PlayerId, out var team) || team != paddle.Team ||
                paddle.Y is not (>= 1.5 and <= 28.5) || paddle.Velocity is not (>= -72 and <= 72))
                return false;
        }

        return true;
    }

    private static void Check(bool valid)
    {
        if (!valid)
            throw new InvalidDataException("Invalid realtime snapshot.");
    }

    private ref struct Reader(ReadOnlySpan<byte> payload)
    {
        private ReadOnlySpan<byte> remaining = payload;
        public readonly bool IsEmpty => remaining.IsEmpty;
        public byte Byte() => Take(1)[0];
        public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        private long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

        public ReadOnlySpan<byte> Take(int size)
        {
            Check(remaining.Length >= size);
            var result = remaining[..size];
            remaining = remaining[size..];
            return result;
        }

        public double Number()
        {
            var value = BitConverter.Int64BitsToDouble(Int64());
            Check(double.IsFinite(value));
            return value;
        }

        public long SafeInteger()
        {
            var value = Int64();
            Check(value is >= 0 and <= RealtimePacket.MaxSafeInteger);
            return value;
        }

        public long? OptionalTick(long tick)
        {
            var value = Int64();
            Check(value >= -1 && value <= tick);
            return value == -1 ? null : value;
        }
    }
}
