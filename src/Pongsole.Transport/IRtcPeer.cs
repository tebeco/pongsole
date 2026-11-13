using System.Collections.Immutable;

namespace Pongsole.Transport;

public enum RtcPeerState
{
    Connecting,
    Connected,
    Disconnected,
    Failed,
    Closed
}

public interface IRtcChannel
{
    string Label { get; }
    bool IsOpen { get; }
    long BufferedAmount { get; }
    event Action? Opened;
    event Action? Drained;
    event Action? Closed;
    event Action<ReadOnlyMemory<byte>>? Received;
    bool TrySend(ReadOnlySpan<byte> bytes);
}

public interface IRtcPeer : IAsyncDisposable
{
    RtcPeerState State { get; }
    event Action<RtcPeerState>? StateChanged;
    event Action<IRtcChannel>? ChannelReceived;
    IRtcChannel CreateChannel(string label, bool reliable);
    Task<string> CreateOfferAsync(CancellationToken cancellationToken);
    Task<string> AcceptOfferAsync(string sdp, CancellationToken cancellationToken);
    void AcceptAnswer(string sdp);
}

public interface IRtcPeerFactory
{
    IRtcPeer Create(ImmutableArray<RtcIceServer> iceServers);
}
