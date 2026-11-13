using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pongsole.Contracts;
using Pongsole.Transport;
using Pongsole.Transport.Native;

namespace Pongsole.Client;

internal interface IGameConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task<JoinResult> JoinAsync(string name, string? token, CancellationToken cancellationToken);
    bool TrySend(InputState input, long sequence);
}

internal sealed class GameProtocolException(string marker) : Exception(marker);

internal sealed class GameConnection : IGameConnection
{
    private static readonly JsonSerializerOptions Json = CreateJson();
    private readonly HttpClient http;
    private readonly IRtcPeerFactory peers;
    private readonly TimeProvider time;
    private readonly Action<GameSnapshot> onSnapshot;
    private readonly object gate = new();
    private readonly TaskCompletionSource opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<GameSnapshot> firstSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<ControlMessage>? joining;
    private RtcSession? session;
    private IRtcPeer? peer;
    private IRtcChannel? control;
    private IRtcChannel? realtime;
    private SnapshotReassembly? reassembly;
    private long startedAt;
    private long disconnectAt = -1;
    private int disposed;
    private int failed;

    public GameConnection(Uri endpoint, string? code, Action<GameSnapshot> onSnapshot)
        : this(endpoint, code, onSnapshot, new NativeRtcPeerFactory(),
            new HttpClientHandler { AllowAutoRedirect = false }, TimeProvider.System) { }

    internal GameConnection(Uri endpoint, string? code, Action<GameSnapshot> onSnapshot,
        IRtcPeerFactory peers, HttpMessageHandler handler, TimeProvider time)
    {
        this.onSnapshot = onSnapshot;
        this.peers = peers;
        this.time = time;
        http = new HttpClient(handler) { BaseAddress = new Uri(endpoint.GetLeftPart(UriPartial.Authority)) };
        if (code is not null) http.DefaultRequestHeaders.Add("X-Pong-Code", code);
    }

    public bool IsConnected =>
        Volatile.Read(ref disposed) == 0 && Volatile.Read(ref failed) == 0 && opened.Task.IsCompletedSuccessfully &&
        peer?.State is RtcPeerState.Connected or RtcPeerState.Disconnected &&
        (Volatile.Read(ref disconnectAt) is var since && (since < 0 || time.GetElapsedTime(since) < TimeSpan.FromSeconds(3))) &&
        time.GetElapsedTime(startedAt) < TimeSpan.FromMinutes(50) &&
        control?.IsOpen == true && realtime?.IsOpen == true;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        startedAt = time.GetTimestamp();
        using var budget = ConnectBudget(cancellationToken);
        session = await RequestAsync<RtcSession>(HttpMethod.Post, "/rtc/sessions", null, null, budget.Token);
        if (session.Epoch is <= 0 or > RealtimePacket.MaxSafeInteger ||
            !ValidId(session.SessionId) || string.IsNullOrEmpty(session.SessionToken))
            throw new IOException("Invalid WebRTC session response.");

        reassembly = new SnapshotReassembly(session.Epoch, time);
        try { peer = peers.Create(session.IceServers); }
        catch (Exception error) when (error is PlatformNotSupportedException or IOException or DllNotFoundException or BadImageFormatException)
        {
            throw new ClientFailure("Native WebRTC is unavailable. Use a supported build with its packaged native library.");
        }
        peer.StateChanged += StateChanged;
        control = peer.CreateChannel("control", reliable: true);
        realtime = peer.CreateChannel("realtime", reliable: false);
        control.Opened += CheckOpened;
        realtime.Opened += CheckOpened;
        control.Closed += Disconnected;
        realtime.Closed += Disconnected;
        control.Received += ReceiveControl;
        realtime.Received += ReceiveRealtime;

        var offer = await peer.CreateOfferAsync(budget.Token);
        var answer = await RequestAsync<RtcDescription>(HttpMethod.Post, SessionPath + "/offer", new RtcDescription(offer),
            session.SessionToken, budget.Token);
        peer.AcceptAnswer(answer.Sdp);
        CheckOpened();
        await opened.Task.WaitAsync(budget.Token);
    }

    public async Task<JoinResult> JoinAsync(string name, string? token, CancellationToken cancellationToken)
    {
        using var budget = ConnectBudget(cancellationToken);
        TaskCompletionSource<ControlMessage> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) joining = response;
        try
        {
            if (control?.TrySend(JsonSerializer.SerializeToUtf8Bytes(new ControlMessage("join", name, token), Json)) != true)
                throw new IOException("Could not send the WebRTC join request.");

            var result = await response.Task.WaitAsync(budget.Token);
            var snapshot = await firstSnapshot.Task.WaitAsync(budget.Token);
            if (result is not { PlayerId: Guid id, Team: Team team, ResumeToken: { Length: > 0 } resume, LastProcessedSequence: long sequence })
                throw new IOException("Invalid WebRTC join response.");
            return new JoinResult(id, team, resume, sequence, snapshot);
        }
        finally
        {
            lock (gate)
            {
                if (joining == response) joining = null;
            }
        }
    }

    public bool TrySend(InputState input, long sequence)
    {
        if (!IsConnected || session is null || realtime is null) return false;

        Span<byte> body = stackalloc byte[InputState.PayloadSize];
        input.Write(body);
        Span<byte> packet = stackalloc byte[RealtimePacket.HeaderSize + InputState.PayloadSize];
        RealtimePacket.Write(packet, RealtimeKind.Input, session.Epoch, sequence, body);
        return realtime.TrySend(packet);
    }

    private void ReceiveControl(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Length > ControlMessage.MaxMessageBytes) return;
            var message = JsonSerializer.Deserialize<ControlMessage>(bytes.Span, Json);
            lock (gate)
            {
                if (message?.Type == "joined") joining?.TrySetResult(message);
                else if (message?.Type == "error")
                {
                    var error = new GameProtocolException(message.Code ?? "");
                    joining?.TrySetException(error);
                    if (joining is null) Disconnected();
                }
            }
        }
        catch (JsonException) { Disconnected(); }
    }

    private void ReceiveRealtime(ReadOnlyMemory<byte> bytes)
    {
        if (Volatile.Read(ref disposed) != 0 || session is null ||
            !RealtimePacket.TryRead(bytes.Span, out var header) || header.Epoch != session.Epoch)
            return;

        if (header.Kind == RealtimeKind.Probe)
        {
            Span<byte> echo = stackalloc byte[RealtimePacket.HeaderSize];
            RealtimePacket.Write(echo, RealtimeKind.Echo, header.Epoch, header.MessageId, []);
            realtime?.TrySend(echo);
            return;
        }

        var completed = reassembly?.Accept(bytes.Span);
        if (completed is not null && SnapshotCodec.TryRead(completed, out var snapshot) && snapshot is not null &&
            snapshot.Arena.Tick == header.MessageId)
        {
            onSnapshot(snapshot);
            firstSnapshot.TrySetResult(snapshot);
        }
    }

    private void CheckOpened()
    {
        if (control?.IsOpen == true && realtime?.IsOpen == true) opened.TrySetResult();
    }

    private void StateChanged(RtcPeerState state)
    {
        if (state == RtcPeerState.Connected) Volatile.Write(ref disconnectAt, -1);
        else if (state == RtcPeerState.Disconnected) Interlocked.CompareExchange(ref disconnectAt, time.GetTimestamp(), -1);
        else if (state is RtcPeerState.Closed or RtcPeerState.Failed) Disconnected();
    }

    private void Disconnected()
    {
        Interlocked.Exchange(ref failed, 1);
        var error = new IOException("The WebRTC connection closed.");
        opened.TrySetException(error);
        firstSnapshot.TrySetException(error);
        lock (gate) joining?.TrySetException(error);
    }

    private CancellationTokenSource ConnectBudget(CancellationToken cancellationToken)
    {
        var remaining = TimeSpan.FromSeconds(30) - time.GetElapsedTime(startedAt);
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        return timeout;
    }

    private string SessionPath => "/rtc/sessions/" + Uri.EscapeDataString(session!.SessionId);

    private async Task<T> RequestAsync<T>(HttpMethod method, string path, object? body, string? token, CancellationToken stop)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (token is not null) request.Headers.Add("X-Pong-Session", token);
        using var response = await http.SendAsync(request, stop);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                throw new ClientFailure("TURN is not configured or the relay service is unavailable.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new GameProtocolException("PONGSOLE_CAPACITY_REACHED");
            throw new HttpRequestException("Game access request failed.", null, response.StatusCode);
        }

        if (method == HttpMethod.Delete) return default!;
        return await response.Content.ReadFromJsonAsync<T>(Json, stop) ?? throw new IOException("Empty WebRTC response.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try
        {
            control?.TrySend(JsonSerializer.SerializeToUtf8Bytes(new ControlMessage("leave"), Json));
            if (peer is not null) await peer.DisposeAsync();
        }
        finally
        {
            if (session is { } current && ValidId(current.SessionId))
            {
                try
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await RequestAsync<object>(HttpMethod.Delete, SessionPath, null, current.SessionToken, stop.Token);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or ClientFailure or GameProtocolException) { }
            }
            http.Dispose();
        }
    }

    private static bool ValidId(string id) =>
        id is { Length: >= 16 and <= 128 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static JsonSerializerOptions CreateJson()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        GameJson.Configure(options);
        return options;
    }
}
