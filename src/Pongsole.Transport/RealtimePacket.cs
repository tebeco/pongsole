using System.Buffers.Binary;

namespace Pongsole.Transport;

public enum RealtimeKind : byte
{
    Snapshot = 1,
    Input = 2,
    Probe = 3,
    Echo = 4
}

public readonly record struct RealtimeHeader(
    RealtimeKind Kind, long Epoch, long MessageId, int ChunkIndex, int ChunkCount, int PayloadLength);

public static class RealtimePacket
{
    private const ushort Magic = 0x4750;
    private const byte Version = 2;
    public const long MaxSafeInteger = 9_007_199_254_740_991;
    public const int HeaderSize = 28;
    public const int MaxPacketSize = 1000;
    public const int ChunkSize = MaxPacketSize - HeaderSize;
    public const int MaxPayloadSize = 8192;

    public static bool TryRead(ReadOnlySpan<byte> packet, out RealtimeHeader header)
    {
        header = default;
        if (packet.Length is < HeaderSize or > MaxPacketSize ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet) != Magic || packet[2] != Version)
            return false;

        var kind = (RealtimeKind)packet[3];
        var epoch = BinaryPrimitives.ReadInt64LittleEndian(packet[4..]);
        var id = BinaryPrimitives.ReadInt64LittleEndian(packet[12..]);
        var index = BinaryPrimitives.ReadUInt16LittleEndian(packet[20..]);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(packet[22..]);
        var size = BinaryPrimitives.ReadInt32LittleEndian(packet[24..]);
        if (!ValidMetadata(kind, epoch, id, index, count, size) ||
            packet.Length != HeaderSize + ChunkLength(size, index))
            return false;

        header = new RealtimeHeader(kind, epoch, id, index, count, size);
        return true;
    }

    public static int Write(Span<byte> destination, RealtimeKind kind, long epoch, long id,
        ReadOnlySpan<byte> payload, int chunkIndex = 0)
    {
        var count = CountChunks(payload.Length);
        if (!ValidMetadata(kind, epoch, id, chunkIndex, count, payload.Length))
            throw new ArgumentException("Invalid realtime packet metadata.");
        var length = ChunkLength(payload.Length, chunkIndex);
        if (destination.Length < HeaderSize + length)
            throw new ArgumentException("Realtime packet destination is too small.", nameof(destination));

        BinaryPrimitives.WriteUInt16LittleEndian(destination, Magic);
        destination[2] = Version;
        destination[3] = (byte)kind;
        BinaryPrimitives.WriteInt64LittleEndian(destination[4..], epoch);
        BinaryPrimitives.WriteInt64LittleEndian(destination[12..], id);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[20..], (ushort)chunkIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[22..], (ushort)count);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], payload.Length);
        payload.Slice(chunkIndex * ChunkSize, length).CopyTo(destination[HeaderSize..]);
        return HeaderSize + length;
    }

    public static int CountChunks(int payloadLength) => payloadLength == 0 ? 1 : 1 + (payloadLength - 1) / ChunkSize;

    private static int ChunkLength(int payloadLength, int index) =>
        Math.Min(ChunkSize, payloadLength - index * ChunkSize);

    private static bool ValidMetadata(RealtimeKind kind, long epoch, long id, int index, int count, int size) =>
        epoch is > 0 and <= MaxSafeInteger && id is >= 0 and <= MaxSafeInteger &&
        size is >= 0 and <= MaxPayloadSize && count == CountChunks(size) && index >= 0 && index < count &&
        (kind switch
        {
            RealtimeKind.Snapshot => size > 0,
            RealtimeKind.Input => size == InputState.PayloadSize && id > 0,
            RealtimeKind.Probe or RealtimeKind.Echo => size == 0,
            _ => false
        });
}
