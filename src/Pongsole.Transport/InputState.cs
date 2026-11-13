using System.Buffers.Binary;
using Pongsole.Contracts;

namespace Pongsole.Transport;

public readonly record struct InputState(Direction? Direction, bool IsPulse, long PulseId)
{
    public const int PayloadSize = 10;

    public static bool TryRead(ReadOnlySpan<byte> source, out InputState state)
    {
        state = default;
        if (source.Length != PayloadSize || source[0] > 2 || source[1] > 1)
            return false;

        var pulseId = BinaryPrimitives.ReadInt64LittleEndian(source[2..]);
        if (!IsValid(source[0], source[1] == 1, pulseId))
            return false;

        state = new InputState(source[0] == 0 ? null : (Direction)(source[0] - 1), source[1] == 1, pulseId);
        return true;
    }

    public void Write(Span<byte> destination)
    {
        var direction = Direction switch
        {
            null => 0,
            Contracts.Direction.Up => 1,
            Contracts.Direction.Down => 2,
            _ => -1
        };
        if (destination.Length < PayloadSize || !IsValid(direction, IsPulse, PulseId))
            throw new ArgumentException("Invalid realtime input or destination.");

        destination[0] = (byte)direction;
        destination[1] = IsPulse ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(destination[2..], PulseId);
    }

    private static bool IsValid(int direction, bool pulse, long pulseId) =>
        direction >= 0 && (pulse
            ? direction != 0 && pulseId is > 0 and <= RealtimePacket.MaxSafeInteger
            : pulseId == 0);
}
