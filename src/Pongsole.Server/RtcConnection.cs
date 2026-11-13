using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Pongsole.Contracts;
using Pongsole.Transport;

namespace Pongsole.Server;

internal sealed class RtcConnection
{
    private const int MaxControlBuffer = 16 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisconnectGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(50);
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private readonly Lock gate = new();
    private readonly GameCoordinator.ConnectionLease lease;
    private readonly GameCoordinator coordinator;
    private readonly TimeProvider time;
    private readonly Action<RtcConnection> remove;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ITimer establishmentTimer;
    private readonly ITimer disconnectTimer;
    private readonly ITimer lifetimeTimer;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IRtcPeer? peer;
    private IRtcChannel? control;
    private IRtcChannel? realtime;
    private GameSnapshot? pendingSnapshot;
    private TaskCompletionSource<string>? probeReply;
    private long probeId;
    private bool offered;
    private bool joined;
    private bool joining;
    private bool closed;
    private bool sendingSnapshot;

    internal RtcConnection(GameCoordinator.ConnectionLease lease, GameCoordinator coordinator,
        TimeProvider time, Action<RtcConnection> remove)
    {
        this.lease = lease;
        this.coordinator = coordinator;
        this.time = time;
        this.remove = remove;
        Closed = lifetime.Token;
        establishmentTimer = time.CreateTimer(_ => Close(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        disconnectTimer = time.CreateTimer(_ => CloseIfDisconnected(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        lifetimeTimer = time.CreateTimer(_ => Close(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        establishmentTimer.Change(ConnectTimeout, Timeout.InfiniteTimeSpan);
        lifetimeTimer.Change(SessionLifetime, Timeout.InfiniteTimeSpan);
    }

    internal string Id => lease.ConnectionId;
    internal long Epoch => lease.Epoch;
    internal string SessionToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    internal CancellationToken Closed { get; }
    internal Task Completion => completion.Task;

    internal bool Attach(IRtcPeer candidate)
    {
        lock (gate)
        {
            if (!closed && peer is null)
            {
                peer = candidate;
                candidate.ChannelReceived += ReceiveChannel;
                candidate.StateChanged += StateChanged;
                return true;
            }
        }

        _ = DisposePeerAsync(candidate);
        return false;
    }

    internal bool TryBeginOffer(out IRtcPeer candidate)
    {
        lock (gate)
        {
            candidate = peer!;
            if (closed || offered || peer is null)
                return false;

            offered = true;
            return true;
        }
    }

    private void StateChanged(RtcPeerState state)
    {
        lock (gate)
        {
            if (closed)
                return;

            if (state is RtcPeerState.Failed or RtcPeerState.Closed)
                Close();
            else if (state == RtcPeerState.Disconnected)
                disconnectTimer.Change(DisconnectGrace, Timeout.InfiniteTimeSpan);
            else if (state == RtcPeerState.Connected)
            {
                disconnectTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                CompleteEstablishment();
            }
        }
    }

    private void CloseIfDisconnected()
    {
        lock (gate)
        {
            if (peer?.State == RtcPeerState.Disconnected)
                Close();
        }
    }

    private void ReceiveChannel(IRtcChannel channel)
    {
        lock (gate)
        {
            if (closed)
                return;

            switch (channel.Label)
            {
                case "control" when control is null:
                    control = channel;
                    channel.Received += ReceiveControl;
                    channel.Opened += CompleteEstablishment;
                    break;
                case "realtime" when realtime is null:
                    realtime = channel;
                    channel.Received += ReceiveRealtime;
                    channel.Drained += FlushSnapshot;
                    channel.Opened += FlushSnapshot;
                    channel.Opened += CompleteEstablishment;
                    break;
                default:
                    Close();
                    return;
            }

            channel.Closed += Close;
            CompleteEstablishment();
        }
    }

    private void CompleteEstablishment()
    {
        lock (gate)
        {
            if (!closed && joined && peer?.State == RtcPeerState.Connected &&
                control is { IsOpen: true } && realtime is { IsOpen: true })
                establishmentTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void ReceiveControl(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > ControlMessage.MaxMessageBytes)
        {
            Close();
            return;
        }

        ControlMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<ControlMessage>(bytes.Span, Json);
        }
        catch (JsonException)
        {
            Close();
            return;
        }

        lock (gate)
        {
            if (closed)
                return;

            if (message is { Type: "leave" })
                Close();
            else if (message is not { Type: "join" })
                Close();
            else if (joining || joined)
                SendControl(new ControlMessage("error", Code: "PONGSOLE_ALREADY_JOINED"));
            else
            {
                joining = true;
                _ = JoinAsync(message);
            }
        }
    }

    private async Task JoinAsync(ControlMessage message)
    {
        try
        {
            var result = await coordinator.JoinAsync(lease, message.Name ?? "", message.ResumeToken);
            lock (gate)
            {
                if (closed)
                    return;

                joined = true;
                CompleteEstablishment();
                SendControl(new ControlMessage("joined", ResumeToken: result.ResumeToken,
                    PlayerId: result.PlayerId, Team: result.Team, LastProcessedSequence: result.LastProcessedSequence));
                if (closed)
                    return;

                pendingSnapshot = result.Snapshot;
                FlushSnapshot();
                _ = PublishSnapshotsAsync();
                _ = LatencyMonitor.RunAsync(ProbeAsync,
                    (milliseconds, measuredAt) => coordinator.UpdateLatency(lease, milliseconds, measuredAt), time, Closed);
            }
        }
        catch (HubException error)
        {
            lock (gate)
            {
                joining = false;
                if (!closed)
                    SendControl(new ControlMessage("error", Code: error.Message));
            }
        }
        catch (Exception)
        {
            Close();
        }
    }

    private void SendControl(ControlMessage message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        try
        {
            if (control is not { IsOpen: true } channel ||
                channel.BufferedAmount > MaxControlBuffer - bytes.Length || !channel.TrySend(bytes))
                Close();
        }
        catch (Exception)
        {
            Close();
        }
    }

    private void ReceiveRealtime(ReadOnlyMemory<byte> bytes)
    {
        if (!RealtimePacket.TryRead(bytes.Span, out var header) || header.Epoch != Epoch)
            return;

        lock (gate)
        {
            if (closed || !joined)
                return;

            switch (header.Kind)
            {
                case RealtimeKind.Input when InputState.TryRead(bytes.Span[RealtimePacket.HeaderSize..], out var input):
                    coordinator.ReceiveInput(lease, Epoch, header.MessageId, input);
                    break;
                case RealtimeKind.Echo when header.MessageId == probeId:
                    probeReply?.TrySetResult(probeId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case RealtimeKind.Probe:
                    TrySendRealtime(RealtimeKind.Echo, header.MessageId);
                    break;
            }
        }
    }

    private async Task PublishSnapshotsAsync()
    {
        try
        {
            await foreach (var snapshot in lease.Snapshots.Reader.ReadAllAsync(Closed))
                PublishSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (Closed.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            Close();
        }
    }

    internal void PublishSnapshot(GameSnapshot snapshot)
    {
        lock (gate)
        {
            if (closed || !joined)
                return;

            pendingSnapshot = snapshot;
            FlushSnapshot();
        }
    }

    private void FlushSnapshot()
    {
        lock (gate)
        {
            if (closed || sendingSnapshot || pendingSnapshot is not { } snapshot ||
                realtime is not { IsOpen: true, BufferedAmount: 0 } channel)
                return;

            sendingSnapshot = true;
            try
            {
                var payload = SnapshotCodec.Write(snapshot);
                Span<byte> packet = stackalloc byte[RealtimePacket.MaxPacketSize];
                var batch = new byte[RealtimePacket.CountChunks(payload.Length)][];
                for (var index = 0; index < batch.Length; index++)
                {
                    var length = RealtimePacket.Write(packet, RealtimeKind.Snapshot, Epoch, snapshot.Arena.Tick, payload, index);
                    batch[index] = packet[..length].ToArray();
                }

                if (channel.TrySendBatch(batch))
                    pendingSnapshot = null;
            }
            catch (Exception)
            {
                Close();
            }
            finally
            {
                sendingSnapshot = false;
            }
        }
    }

    private async Task<string> ProbeAsync(string nonce, CancellationToken cancellationToken)
    {
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            probeReply = reply;
            var bytes = Convert.FromHexString(nonce);
            probeId = (long)(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes) &
                (ulong)RealtimePacket.MaxSafeInteger);
            if (!TrySendRealtime(RealtimeKind.Probe, probeId))
                reply.TrySetResult("");
        }

        try
        {
            return await reply.Task.WaitAsync(cancellationToken) == "" ? "" : nonce;
        }
        finally
        {
            lock (gate)
                probeReply = null;
        }
    }

    private bool TrySendRealtime(RealtimeKind kind, long messageId)
    {
        if (realtime is not { IsOpen: true, BufferedAmount: 0 } channel)
            return false;

        Span<byte> packet = stackalloc byte[RealtimePacket.HeaderSize];
        RealtimePacket.Write(packet, kind, Epoch, messageId, []);
        try
        {
            return channel.TrySend(packet);
        }
        catch (Exception)
        {
            Close();
            return false;
        }
    }

    internal void Close()
    {
        lock (gate)
        {
            if (closed)
                return;

            closed = true;
            _ = Task.Run(CleanupAsync);
        }
    }

    private async Task CleanupAsync()
    {
        try
        {
            await lifetime.CancelAsync();
            establishmentTimer.Dispose();
            disconnectTimer.Dispose();
            lifetimeTimer.Dispose();
            var disconnect = coordinator.DisconnectAsync(lease);
            if (peer is { } candidate)
                await DisposePeerAsync(candidate);
            await disconnect;
        }
        catch (Exception)
        {
            // Transport shutdown and coordinator shutdown may race.
        }
        finally
        {
            remove(this);
            lifetime.Dispose();
            completion.TrySetResult();
        }
    }

    private static async Task DisposePeerAsync(IRtcPeer candidate)
    {
        try
        {
            await candidate.DisposeAsync();
        }
        catch (Exception)
        {
            // Native shutdown failures may contain transport details.
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        GameJson.Configure(options);
        return options;
    }
}
