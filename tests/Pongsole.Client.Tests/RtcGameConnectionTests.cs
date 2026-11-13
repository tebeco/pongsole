using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Pongsole.Contracts;
using Pongsole.Transport;

namespace Pongsole.Client.Tests;

public sealed class RtcGameConnectionTests
{
    [Fact]
    public async Task SignalingUsesHeaderTokensAndWaitsForFirstValidSnapshot()
    {
        var wire = new TestPeer();
        var http = new SignalingHandler();
        var rendered = new List<GameSnapshot>();
        await using (var connection = new GameConnection(new Uri("https://localhost/game"), "code", rendered.Add,
            wire, http, TimeProvider.System))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await connection.StartAsync(timeout.Token);
            Assert.True(connection.IsConnected);
            var join = connection.JoinAsync("Player", null, timeout.Token);
            wire.Control.Emit("""{"type":"joined","playerId":"1f730f5a-8787-4d82-a3e9-c34833fa4b2a","team":"left","resumeToken":"resume","lastProcessedSequence":4}"""u8.ToArray());
            Assert.False(join.IsCompleted);
            var snapshot = SnapshotHistoryTests.Snapshot(12, 50, []);
            var bytes = SnapshotCodec.Write(snapshot);
            var packet = new byte[RealtimePacket.MaxPacketSize];
            var length = RealtimePacket.Write(packet, RealtimeKind.Snapshot, 2, 12, bytes);
            wire.Realtime.Emit(packet.AsMemory(0, length));
            Assert.False(join.IsCompleted);
            length = RealtimePacket.Write(packet, RealtimeKind.Snapshot, 1, 12, bytes);
            wire.Realtime.Emit(packet.AsMemory(0, length));
            Assert.Equal(12, (await join).Snapshot.Arena.Tick);
            Assert.Single(rendered);

            Assert.True(connection.TrySend(new InputState(Direction.Down, true, 5), 5));
            var input = Assert.Single(wire.Realtime.Sent);
            Assert.True(RealtimePacket.TryRead(input, out var header));
            Assert.Equal(RealtimeKind.Input, header.Kind);
            Assert.Equal(5, header.MessageId);
        }

        Assert.Equal(new[] { "/rtc/sessions", "/rtc/sessions/abcdefghijklmnop/offer", "/rtc/sessions/abcdefghijklmnop" },
            http.Requests.Select(request => request.Path));
        Assert.Equal(new string?[] { null, "token", "token" }, http.Requests.Select(request => request.Token));
        Assert.All(http.Requests, request => Assert.Equal("code", request.Code));
        Assert.True(wire.Disposed);
    }

    private sealed class SignalingHandler : HttpMessageHandler
    {
        public List<(string Path, string? Token, string Code)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Empty(request.RequestUri!.Query);
            var path = request.RequestUri.AbsolutePath;
            Requests.Add((path, request.Headers.TryGetValues("X-Pong-Session", out var token) ? Assert.Single(token) : null,
                Assert.Single(request.Headers.GetValues("X-Pong-Code"))));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = path.EndsWith("/offer", StringComparison.Ordinal) ? JsonContent.Create(new RtcDescription("answer")) :
                    JsonContent.Create(new RtcSession("abcdefghijklmnop", "token", 1, [], DateTimeOffset.UtcNow.AddMinutes(1)))
            });
        }
    }

    private sealed class TestPeer : IRtcPeerFactory, IRtcPeer
    {
        public TestChannel Control { get; } = new("control");
        public TestChannel Realtime { get; } = new("realtime");
        public bool Disposed { get; private set; }
        public RtcPeerState State => RtcPeerState.Connected;
        public event Action<RtcPeerState>? StateChanged { add { } remove { } }
        public event Action<IRtcChannel>? ChannelReceived { add { } remove { } }
        public IRtcPeer Create(ImmutableArray<RtcIceServer> iceServers) => this;
        public IRtcChannel CreateChannel(string label, bool reliable) => (label, reliable) switch
        {
            ("control", true) => Control,
            ("realtime", false) => Realtime,
            _ => throw new InvalidOperationException()
        };
        public Task<string> CreateOfferAsync(CancellationToken cancellationToken) => Task.FromResult("offer");
        public Task<string> AcceptOfferAsync(string sdp, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void AcceptAnswer(string sdp) => Assert.Equal("answer", sdp);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class TestChannel(string label) : IRtcChannel
    {
        public string Label => label;
        public bool IsOpen => true;
        public long BufferedAmount => 0;
        public List<byte[]> Sent { get; } = [];
        public event Action? Opened { add { } remove { } }
        public event Action? Drained { add { } remove { } }
        public event Action? Closed { add { } remove { } }
        public event Action<ReadOnlyMemory<byte>>? Received;
        public bool TrySend(ReadOnlySpan<byte> bytes) { Sent.Add(bytes.ToArray()); return true; }
        public bool TrySendBatch(IReadOnlyList<byte[]> packets) => throw new NotSupportedException();
        public void Emit(ReadOnlyMemory<byte> bytes) => Received?.Invoke(bytes);
    }
}
