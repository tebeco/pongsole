using System.Net;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Pongsole.Transport;

namespace Pongsole.Server;

internal sealed class RtcSessions(GameCoordinator coordinator, TimeProvider time) : IHostedService
{
    private const int MaximumTrackedAddresses = 4096;
    private const int StartsPerMinute = 5;
    private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);
    private readonly Lock gate = new();
    private readonly Dictionary<string, RtcConnection> sessions = [];
    private readonly Dictionary<IPAddress, Queue<long>> starts = [];
    private bool stopping;

    internal (RtcConnection? Connection, string? Error) Reserve(IPAddress? address, DiscordPlayer? player = null)
    {
        lock (gate)
        {
            if (stopping)
                return (null, "PONGSOLE_SERVER_STOPPING");
            if (sessions.Count >= 64)
                return (null, "PONGSOLE_CAPACITY_REACHED");

            var now = time.GetTimestamp();
            address ??= IPAddress.None;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            if (!starts.TryGetValue(address, out var timestamps))
            {
                if (starts.Count >= MaximumTrackedAddresses)
                {
                    foreach (var entry in starts.Where(entry =>
                        time.GetElapsedTime(entry.Value.Last(), now) >= RateWindow).ToArray())
                        starts.Remove(entry.Key);
                }
                if (starts.Count >= MaximumTrackedAddresses)
                    return (null, "PONGSOLE_RTC_RATE_LIMIT");

                timestamps = new Queue<long>(StartsPerMinute);
                starts.Add(address, timestamps);
            }

            while (timestamps.TryPeek(out var timestamp) && time.GetElapsedTime(timestamp, now) >= RateWindow)
                timestamps.Dequeue();
            if (timestamps.Count >= StartsPerMinute)
                return (null, "PONGSOLE_RTC_RATE_LIMIT");

            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            GameCoordinator.ConnectionLease lease;
            try
            {
                lease = coordinator.Admit(id, NewEpoch(), player);
            }
            catch (HubException error)
            {
                return (null, error.Message);
            }

            timestamps.Enqueue(now);
            var connection = new RtcConnection(lease, coordinator, time, Remove);
            sessions.Add(id, connection);
            return (connection, null);
        }
    }

    private static long NewEpoch()
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        long value;
        do
        {
            RandomNumberGenerator.Fill(bytes);
            value = BinaryPrimitives.ReadInt64LittleEndian(bytes) & RealtimePacket.MaxSafeInteger;
        }
        while (value == 0);
        return value;
    }

    internal RtcConnection? Find(string id, string token)
    {
        if (id.Length != 32 || token.Length != 64)
            return null;

        lock (gate)
        {
            return sessions.TryGetValue(id, out var connection) &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(connection.SessionToken))
                ? connection
                : null;
        }
    }

    private void Remove(RtcConnection connection)
    {
        lock (gate)
            sessions.Remove(connection.Id);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RtcConnection[] active;
        lock (gate)
        {
            stopping = true;
            active = [.. sessions.Values];
        }

        foreach (var connection in active)
            connection.Close();
        await Task.WhenAll(active.Select(connection => connection.Completion)).WaitAsync(cancellationToken);
    }
}
