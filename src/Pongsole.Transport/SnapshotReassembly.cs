namespace Pongsole.Transport;

public sealed class SnapshotReassembly(long epoch, TimeProvider time)
{
    private static readonly TimeSpan PartialLifetime = TimeSpan.FromMilliseconds(500);
    private long highest = -1;
    private byte[]? body;
    private int received;
    private int expectedChunks;
    private long startedAt;

    public byte[]? Accept(ReadOnlySpan<byte> packet)
    {
        if (!RealtimePacket.TryRead(packet, out var header) || header.Kind != RealtimeKind.Snapshot ||
            header.Epoch != epoch || header.MessageId < highest)
            return null;

        var now = time.GetTimestamp();
        if (header.MessageId > highest)
        {
            highest = header.MessageId;
            body = new byte[header.PayloadLength];
            received = 0;
            expectedChunks = header.ChunkCount;
            startedAt = now;
        }
        else if (body is not null && (time.GetElapsedTime(startedAt, now) >= PartialLifetime ||
            body.Length != header.PayloadLength || expectedChunks != header.ChunkCount))
        {
            body = null;
        }

        if (body is null)
            return null;

        var payload = packet[RealtimePacket.HeaderSize..];
        var chunk = body.AsSpan(header.ChunkIndex * RealtimePacket.ChunkSize, payload.Length);
        var bit = 1 << header.ChunkIndex;
        if ((received & bit) != 0)
        {
            if (!payload.SequenceEqual(chunk))
                body = null;
            return null;
        }

        payload.CopyTo(chunk);
        received |= bit;
        if (received != (1 << expectedChunks) - 1)
            return null;

        var completed = body;
        body = null;
        return completed;
    }
}
