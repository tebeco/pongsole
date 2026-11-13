using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Pongsole.Transport.Native;

public sealed class NativeRtcPeerFactory : IRtcPeerFactory
{
    public IRtcPeer Create(ImmutableArray<RtcIceServer> iceServers)
    {
        NativeRuntime.EnsureLoaded();
        return new NativeRtcPeer(iceServers);
    }
}

internal sealed class NativeRtcPeer : IRtcPeer
{
    private static readonly ConcurrentDictionary<nint, NativeRtcPeer> Owners = new();
    private static long nextOwner;
    private static readonly NativeMethods.State StateCallback = (_, state, owner) => Dispatch(owner, p => p.OnState(state));
    private static readonly NativeMethods.State GatheringCallback = (_, state, owner) => Dispatch(owner, p =>
    {
        if (state == 2) p.gathered.TrySetResult();
    });
    private static readonly NativeMethods.State ChannelCallback = (peerId, channel, owner) =>
    {
        try
        {
            if (Owners.TryGetValue(owner, out var peer) && Volatile.Read(ref peer.disposed) == 0)
            {
                peer.incomingIds.TryAdd(channel, 0);
                if (Volatile.Read(ref peer.disposed) == 0)
                {
                    peer.Enqueue(() => peer.AddIncoming(channel));
                    return;
                }
                if (!peer.incomingIds.TryRemove(channel, out _)) return;
            }
            ThreadPool.QueueUserWorkItem(static id => NativeMethods.rtcDeleteDataChannel(id), channel, preferLocal: false);
        }
        catch { }
    };
    private static readonly NativeMethods.Simple OpenCallback = (channel, owner) => Dispatch(owner, p => p.Enqueue(() => p.WithChannel(channel, c => c.MarkOpen())));
    private static readonly NativeMethods.Simple ClosedCallback = (channel, owner) => Dispatch(owner, p => p.Enqueue(() => p.WithChannel(channel, c => c.MarkClosed())));
    private static readonly NativeMethods.Simple DrainCallback = (channel, owner) => Dispatch(owner, p => p.Enqueue(() => p.WithChannel(channel, c => c.NotifyDrained())));
    private static readonly NativeMethods.Message MessageCallback = (channel, pointer, size, owner) =>
        Dispatch(owner, p => p.OnMessage(channel, pointer, size));

    private readonly object gate = new();
    private readonly nint owner = (nint)Interlocked.Increment(ref nextOwner);
    private readonly ConcurrentDictionary<int, NativeChannel> channels = new();
    private readonly ConcurrentDictionary<int, byte> incomingIds = new();
    private readonly Channel<Action> control = Channel.CreateBounded<Action>(8);
    private readonly Channel<Action> realtime = Channel.CreateBounded<Action>(16);
    private readonly SemaphoreSlim ready = new(0, 1);
    private readonly TaskCompletionSource gathered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task dispatch;
    private readonly int peer;
    private int disposed;
    private int failed;
    private int state = (int)RtcPeerState.Connecting;
    private bool described;

    internal NativeRtcPeer(ImmutableArray<RtcIceServer> servers)
    {
        var urls = servers.SelectMany(server => server.Urls.Select(url => TurnUrl(url, server))).Take(4).ToArray();
        if (urls.Length == 0) throw new ArgumentException("A relay UDP TURN server is required.", nameof(servers));

        peer = CreateNativePeer(urls);
        Owners.TryAdd(owner, this);
        try
        {
            NativeMethods.rtcSetUserPointer(peer, owner);
            NativeMethods.Check(NativeMethods.rtcSetGatheringStateChangeCallback(peer, GatheringCallback));
            NativeMethods.Check(NativeMethods.rtcSetStateChangeCallback(peer, StateCallback));
            NativeMethods.Check(NativeMethods.rtcSetDataChannelCallback(peer, ChannelCallback));
        }
        catch
        {
            Owners.TryRemove(owner, out _);
            NativeMethods.rtcDeletePeerConnection(peer);
            throw;
        }

        dispatch = Task.Run(DispatchAsync);
    }

    public RtcPeerState State => (RtcPeerState)Volatile.Read(ref state);
    public event Action<RtcPeerState>? StateChanged;
    public event Action<IRtcChannel>? ChannelReceived;

    private static string TurnUrl(string url, RtcIceServer server)
    {
        if (url is not ("turn:turn.cloudflare.com:3478?transport=udp" or "turn:turn.cloudflare.com:443?transport=udp")
            || string.IsNullOrEmpty(server.Username) || string.IsNullOrEmpty(server.Credential))
            throw new ArgumentException("An unsupported TURN endpoint or credential was supplied.");
        return "turn:" + Uri.EscapeDataString(server.Username) + ":" + Uri.EscapeDataString(server.Credential) + "@" + url[5..];
    }

    private static int CreateNativePeer(string[] urls)
    {
        var array = Marshal.AllocHGlobal(urls.Length * IntPtr.Size);
        var pointers = new nint[urls.Length];
        try
        {
            for (var index = 0; index < urls.Length; index++)
            {
                pointers[index] = Marshal.StringToCoTaskMemUTF8(urls[index]);
                Marshal.WriteIntPtr(array, index * IntPtr.Size, pointers[index]);
            }

            return NativeMethods.Check(NativeMethods.rtcCreatePeerConnection(new NativeMethods.Configuration
            {
                Servers = array, ServerCount = urls.Length, Policy = 1, ManualNegotiation = 1,
                MaxMessage = ControlMessage.MaxMessageBytes
            }));
        }
        finally
        {
            foreach (var pointer in pointers) Marshal.FreeCoTaskMem(pointer);
            Marshal.FreeHGlobal(array);
        }
    }

    public IRtcChannel CreateChannel(string label, bool reliable)
    {
        if (!KnownChannel(label, reliable)) throw new ArgumentException("Invalid WebRTC channel configuration.");
        lock (gate)
        {
            ThrowIfDisposed();
            if (channels.Values.Any(channel => channel.Label == label))
                throw new InvalidOperationException("The WebRTC channel already exists.");

            var id = NativeMethods.Check(NativeMethods.rtcCreateDataChannelEx(peer, label, new NativeMethods.ChannelInit
            {
                Reliability = new NativeMethods.Reliability { Unordered = reliable ? (byte)0 : (byte)1,
                    Unreliable = reliable ? (byte)0 : (byte)1 }
            }));
            try
            {
                if (!HasExpectedReliability(id, reliable))
                    throw new IOException("WebRTC channel reliability did not match its configuration.");
                return Register(id, label, reliable);
            }
            catch { DeleteChannel(id); throw; }
        }
    }

    private static bool HasExpectedReliability(int id, bool reliable)
    {
        NativeMethods.Check(NativeMethods.rtcGetDataChannelReliability(id, out var actual));
        return reliable ? actual is { Unordered: 0, Unreliable: 0 } :
            actual is { Unordered: 1, Unreliable: 1, Lifetime: 0, Retransmits: 0 };
    }

    public Task<string> CreateOfferAsync(CancellationToken cancellationToken) => DescribeAsync(null, cancellationToken);
    public Task<string> AcceptOfferAsync(string sdp, CancellationToken cancellationToken) => DescribeAsync(sdp, cancellationToken);

    public void AcceptAnswer(string sdp)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            SetRemote(sdp, "answer");
        }
    }

    private async Task<string> DescribeAsync(string? offer, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (described) throw new InvalidOperationException("The WebRTC peer already began negotiation.");
            described = true;
            if (offer is not null) SetRemote(offer, "offer");
            NativeMethods.Check(NativeMethods.rtcSetLocalDescription(peer, offer is null ? "offer" : "answer"));
        }

        await gathered.Task.WaitAsync(cancellationToken);
        lock (gate)
        {
            ThrowIfDisposed();
            var buffer = new byte[32 * 1024];
            NativeMethods.Check(NativeMethods.rtcGetLocalDescription(peer, buffer, buffer.Length));
            return RelaySdp.Sanitize(NativeMethods.Decode(buffer));
        }
    }

    private void SetRemote(string sdp, string type)
    {
        if (!RelaySdp.IsValid(sdp)) throw new InvalidDataException("Invalid relay-only WebRTC description.");
        NativeMethods.Check(NativeMethods.rtcSetRemoteDescription(peer, sdp, type));
    }

    private void AddIncoming(int id)
    {
        NativeChannel channel;
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            incomingIds.TryRemove(id, out _);
            try
            {
                var labelBytes = new byte[16];
                NativeMethods.Check(NativeMethods.rtcGetDataChannelLabel(id, labelBytes, labelBytes.Length));
                var label = NativeMethods.Decode(labelBytes);
                NativeMethods.Check(NativeMethods.rtcGetDataChannelReliability(id, out var reliability));
                var reliable = reliability is { Unordered: 0, Unreliable: 0 };
                var realtime = reliability is { Unordered: 1, Unreliable: 1, Lifetime: 0, Retransmits: 0 };
                if (!KnownChannel(label, reliable) || (!reliable && !realtime)
                    || channels.Values.Any(existing => existing.Label == label))
                    throw new IOException("An unexpected WebRTC channel was opened.");

                channel = Register(id, label, reliable);
            }
            catch
            {
                DeleteChannel(id);
                throw;
            }
        }

        ChannelReceived?.Invoke(channel);
        if (NativeIsOpen(id)) channel.MarkOpen();
    }

    private NativeChannel Register(int id, string label, bool reliable)
    {
        var channel = new NativeChannel(this, id, label, reliable);
        if (!channels.TryAdd(id, channel)) throw new InvalidOperationException();
        NativeMethods.rtcSetUserPointer(id, owner);
        NativeMethods.Check(NativeMethods.rtcSetOpenCallback(id, OpenCallback));
        NativeMethods.Check(NativeMethods.rtcSetClosedCallback(id, ClosedCallback));
        NativeMethods.Check(NativeMethods.rtcSetBufferedAmountLowThreshold(id, 0));
        NativeMethods.Check(NativeMethods.rtcSetBufferedAmountLowCallback(id, DrainCallback));
        NativeMethods.Check(NativeMethods.rtcSetMessageCallback(id, MessageCallback));
        return channel;
    }

    private void DeleteChannel(int id)
    {
        channels.TryRemove(id, out _);
        NativeMethods.rtcDeleteDataChannel(id);
    }

    private static bool KnownChannel(string label, bool reliable) =>
        (label == "control" && reliable) || (label == "realtime" && !reliable);

    private void WithChannel(int id, Action<NativeChannel> action)
    {
        if (channels.TryGetValue(id, out var channel)) action(channel);
    }

    private void OnMessage(int id, nint pointer, int size)
    {
        if (!channels.TryGetValue(id, out var channel)) return;
        // libdatachannel reports UTF-8 text as -(byte count + 1).
        if (size < 0 && channel.Reliable && size >= -channel.MaxBytes - 1) size = -size - 1;
        if (size <= 0 || size > channel.MaxBytes)
        {
            if (channel.Reliable) Fail();
            return;
        }
        var bytes = new byte[size];
        Marshal.Copy(pointer, bytes, 0, size);
        Enqueue(() => channel.NotifyReceived(bytes), channel.Reliable);
    }

    private void OnState(int value)
    {
        var next = value switch
        {
            2 => RtcPeerState.Connected,
            3 => RtcPeerState.Disconnected,
            4 => RtcPeerState.Failed,
            5 => RtcPeerState.Closed,
            _ => RtcPeerState.Connecting
        };
        Enqueue(() => ChangeState(next));
    }

    private void ChangeState(RtcPeerState next)
    {
        if ((RtcPeerState)Interlocked.Exchange(ref state, (int)next) != next)
            StateChanged?.Invoke(next);
    }

    private static void Dispatch(nint owner, Action<NativeRtcPeer> callback)
    {
        if (!Owners.TryGetValue(owner, out var peer) || Volatile.Read(ref peer.disposed) != 0) return;
        try { callback(peer); }
        catch { peer.Fail(); }
    }

    private void Enqueue(Action action, bool reliable = true)
    {
        if (!(reliable ? control : realtime).Writer.TryWrite(action) && reliable)
            Fail();
        Wake();
    }

    private void Fail()
    {
        Interlocked.Exchange(ref failed, 1);
        Wake();
    }

    private void Wake()
    {
        try { ready.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task DispatchAsync()
    {
        try
        {
            while (Volatile.Read(ref disposed) == 0 && Volatile.Read(ref failed) == 0)
            {
                if (control.Reader.TryRead(out var action) || realtime.Reader.TryRead(out action)) action();
                else await ready.WaitAsync();
            }
        }
        catch { Interlocked.Exchange(ref failed, 1); }
        finally
        {
            if (Volatile.Read(ref failed) != 0)
            {
                gathered.TrySetException(new IOException("WebRTC communication failed."));
                try { ChangeState(RtcPeerState.Failed); }
                catch { }
            }
        }
    }

    private bool NativeIsOpen(int id)
    {
        lock (gate) return Volatile.Read(ref disposed) == 0 && NativeMethods.rtcIsOpen(id);
    }

    private long GetBufferedAmount(int id)
    {
        lock (gate) return Volatile.Read(ref disposed) == 0 ? Math.Max(0, NativeMethods.rtcGetBufferedAmount(id)) : 0;
    }

    private bool TrySend(int id, ReadOnlySpan<byte> bytes, int maxBytes)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || bytes.Length <= 0 || bytes.Length > maxBytes
                || !NativeMethods.rtcIsOpen(id) || NativeMethods.rtcGetBufferedAmount(id) != 0)
                return false;

            return NativeMethods.rtcSendMessage(id, bytes.ToArray(), bytes.Length) >= 0;
        }
    }

    private bool TrySendBatch(int id, IReadOnlyList<byte[]> packets)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 ||
                packets.Count < 1 || packets.Count > RealtimePacket.CountChunks(RealtimePacket.MaxPayloadSize) ||
                !NativeMethods.rtcIsOpen(id) ||
                NativeMethods.rtcGetBufferedAmount(id) != 0)
                return false;

            foreach (var packet in packets)
            {
                if (packet is null || packet.Length is < RealtimePacket.HeaderSize or > RealtimePacket.MaxPacketSize)
                    return false;
            }

            foreach (var packet in packets)
            {
                if (NativeMethods.rtcSendMessage(id, packet, packet.Length) < 0)
                    return false;
            }

            return true;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            Owners.TryRemove(owner, out _);
            gathered.TrySetCanceled();
            Wake();
            _ = FinishDisposeAsync();
        }

        return new ValueTask(closed.Task);
    }

    private async Task FinishDisposeAsync()
    {
        try
        {
            await dispatch;
            lock (gate)
            {
                foreach (var id in channels.Keys) DeleteChannel(id);
                foreach (var id in incomingIds.Keys)
                    if (incomingIds.TryRemove(id, out _)) DeleteChannel(id);
                NativeMethods.rtcDeletePeerConnection(peer);
            }
            try { ChangeState(RtcPeerState.Closed); }
            catch { }
            closed.TrySetResult();
        }
        catch (Exception error) { closed.TrySetException(error); }
    }

    private sealed class NativeChannel(NativeRtcPeer peer, int id, string label, bool reliable) : IRtcChannel
    {
        private int opened;
        public string Label { get; } = label;
        public bool IsOpen => peer.NativeIsOpen(id);
        public long BufferedAmount => peer.GetBufferedAmount(id);
        internal bool Reliable => reliable;
        internal int MaxBytes => reliable ? ControlMessage.MaxMessageBytes : RealtimePacket.MaxPacketSize;
        public event Action? Opened;
        public event Action? Drained;
        public event Action? Closed;
        public event Action<ReadOnlyMemory<byte>>? Received;
        public bool TrySend(ReadOnlySpan<byte> bytes) => peer.TrySend(id, bytes, MaxBytes);
        public bool TrySendBatch(IReadOnlyList<byte[]> packets) => !reliable && peer.TrySendBatch(id, packets);
        internal void MarkOpen()
        {
            if (Interlocked.Exchange(ref opened, 1) == 0) Opened?.Invoke();
        }
        internal void MarkClosed() => Closed?.Invoke();
        internal void NotifyDrained() => Drained?.Invoke();
        internal void NotifyReceived(byte[] bytes) => Received?.Invoke(bytes);
    }
}
