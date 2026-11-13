using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;
using Pongsole.Core;
using Pongsole.Transport;

namespace Pongsole.Server.Tests;

public sealed class GameServerTests
{
    [Fact]
    public async Task RelaySignalingAuthenticatesBeforeMintingAndRequiresSessionToken()
    {
        var credentials = new TestTurnCredentials();
        var peer = new TestRtcPeer();
        await using var server = new TestGameServer(IPAddress.Parse("192.0.2.4"), "Private",
            relayFactory: peer, relayCredentials: credentials);
        server.StartTicks();
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = TestGameServer.Origin(true) });
        using var unauthorized = await client.PostAsync("/rtc/sessions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(0, credentials.Calls);

        client.DefaultRequestHeaders.Add("X-Pong-Code", "test-code");
        using var started = await client.PostAsync("/rtc/sessions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        Assert.True(started.Headers.CacheControl?.NoStore);
        var session = await started.Content.ReadFromJsonAsync<RtcSession>(TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.Equal(2, credentials.Calls);
        Assert.Equal("user-1", Assert.Single(session.IceServers).Username);
        Assert.Equal("user-2", Assert.Single(peer.IceServers).Username);

        var offerPath = $"/rtc/sessions/{session.SessionId}/offer";
        using var missingToken = await client.PostAsJsonAsync(offerPath, new RtcDescription(TestRtcPeer.Sdp),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missingToken.StatusCode);
        client.DefaultRequestHeaders.Add("X-Pong-Session", session.SessionToken);
        using var rejected = await client.PostAsJsonAsync(offerPath,
            new RtcDescription(TestRtcPeer.Sdp.Replace("typ relay", "typ host")), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var answered = await client.PostAsJsonAsync(offerPath, new RtcDescription(TestRtcPeer.Sdp),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        using var duplicate = await client.PostAsJsonAsync(offerPath, new RtcDescription(TestRtcPeer.Sdp),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(1, peer.Offers);
        using var deleted = await client.DeleteAsync($"/rtc/sessions/{session.SessionId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        await peer.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RelayRejectsCrossOriginMissingBrowserMarkerAndBrowserCodeWithoutTicket()
    {
        var credentials = new TestTurnCredentials();
        await using var server = new TestGameServer(IPAddress.Parse("192.0.2.4"), "Private",
            relayFactory: new TestRtcPeer(), relayCredentials: credentials);
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = TestGameServer.Origin(true) });
        client.DefaultRequestHeaders.Add("X-Pong-Code", "test-code");
        client.DefaultRequestHeaders.Add("Origin", "https://unrelated.example");
        using var crossOrigin = await client.PostAsync("/rtc/sessions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);

        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        using var unmarked = await client.PostAsync("/rtc/sessions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, unmarked.StatusCode);
        client.DefaultRequestHeaders.Add("X-Pong-Browser", "1");
        using var noTicket = await client.PostAsync("/rtc/sessions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, noTicket.StatusCode);
        Assert.Equal(0, credentials.Calls);

        using var unlock = await client.PostAsync("/browser/session", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, unlock.StatusCode);
        client.DefaultRequestHeaders.Remove("X-Pong-Code");
        using var ticketed = await client.PostAsync("/rtc/sessions", null, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, ticketed.StatusCode);
        Assert.Equal(2, credentials.Calls);
    }

    [Fact]
    public async Task RelayReservationsLimitStartsPerAddressAndReleaseExpiredAdmissions()
    {
        using var coordinator = new GameCoordinator(new GameEngine(1), new FakeTimeProvider(), new Random(1));
        var time = new FakeTimeProvider();
        var sessions = new RtcSessions(coordinator, time);
        var reservations = new List<RtcConnection>();
        for (var index = 0; index < 64; index++)
        {
            var (connection, error) = sessions.Reserve(new IPAddress([192, 0, 2, (byte)(index / 5)]));
            Assert.Null(error);
            Assert.NotNull(connection);
            reservations.Add(connection);
            if (index == 4)
                Assert.Equal("PONGSOLE_RTC_RATE_LIMIT", sessions.Reserve(IPAddress.Parse("192.0.2.0")).Error);
        }

        Assert.Equal("PONGSOLE_CAPACITY_REACHED", sessions.Reserve(IPAddress.Loopback).Error);
        Assert.Null(sessions.Find(reservations[0].Id, new string('0', 64)));
        Assert.Same(reservations[0], sessions.Find(reservations[0].Id, reservations[0].SessionToken));
        time.Advance(TimeSpan.FromSeconds(30));
        while (reservations.Any(connection => !connection.Completion.IsCompleted))
        {
            coordinator.Tick();
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        Assert.Null(sessions.Find(reservations[0].Id, reservations[0].SessionToken));
        Assert.Equal("PONGSOLE_RTC_RATE_LIMIT", sessions.Reserve(IPAddress.Parse("192.0.2.0")).Error);
        time.Advance(TimeSpan.FromSeconds(30));
        var (replacement, _) = sessions.Reserve(IPAddress.Parse("192.0.2.0"));
        Assert.NotNull(replacement);
        Assert.InRange(replacement.Epoch, 1, RealtimePacket.MaxSafeInteger);
        Assert.DoesNotContain(reservations, reservation => reservation.Epoch == replacement.Epoch);
        replacement.Close();
        while (!replacement.Completion.IsCompleted)
        {
            coordinator.Tick();
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RelayBackpressureKeepsOnlyTheLatestSnapshotAndControlOverflowClosesPeer()
    {
        var time = new FakeTimeProvider();
        var engine = new GameEngine(1);
        using var coordinator = new GameCoordinator(engine, time, new Random(1));
        var sessions = new RtcSessions(coordinator, time);
        var (connection, _) = sessions.Reserve(IPAddress.Loopback);
        Assert.NotNull(connection);
        var peer = new TestRtcPeer();
        Assert.True(connection.Attach(peer));
        Assert.True(connection.TryBeginOffer(out _));
        var control = new TestRtcChannel("control");
        var realtime = new TestRtcChannel("realtime") { BufferedAmount = 1, BufferOnSend = true };
        peer.Open(control);
        peer.Open(realtime);
        control.Receive("""{"type":"join","name":"Player"}"""u8.ToArray());
        coordinator.Tick();
        await control.Sent.Reader.ReadAsync(TestContext.Current.CancellationToken);

        var arena = engine.State with
        {
            Phase = Phase.Playing,
            Tick = 11,
            Balls = [.. Enumerable.Range(1, 10).Select(id => new BallState(id, 50, 15, 24, 0))],
            Pickups = [.. Enumerable.Range(1, 12).Select(id => new PickupState(id, 50, 15, 10, 3600))]
        };
        var players = Enumerable.Range(1, 64).Select(index =>
            new PlayerState(Guid.NewGuid(), $"Player{index:0000}", (Team)(index % 2), index)).ToImmutableArray();
        connection.PublishSnapshot(new GameSnapshot(arena, players));
        connection.PublishSnapshot(new GameSnapshot(arena with { Tick = 12 }, players));

        Assert.False(realtime.Sent.Reader.TryRead(out _));
        realtime.Drain();
        var first = await realtime.Sent.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.True(RealtimePacket.TryRead(first, out var header));
        Assert.True(header.ChunkCount > 1);
        var reassembly = new SnapshotReassembly(connection.Epoch, time);
        Assert.Null(reassembly.Accept(first));
        byte[]? payload = null;
        for (var index = 1; index < header.ChunkCount; index++)
        {
            Assert.True(realtime.Sent.Reader.TryRead(out var chunk));
            payload = reassembly.Accept(chunk);
        }
        Assert.NotNull(payload);
        Assert.True(SnapshotCodec.TryRead(payload, out var snapshot));
        Assert.NotNull(snapshot);
        Assert.Equal(12, snapshot.Arena.Tick);
        Assert.Equal(64, snapshot.Players.Length);
        Assert.Equal(10, snapshot.Arena.Balls.Length);
        Assert.Equal(12, snapshot.Arena.Pickups.Length);
        Assert.False(realtime.Sent.Reader.TryRead(out _));

        control.BufferedAmount = 16 * 1024;
        control.Receive("""{"type":"join","name":"Player"}"""u8.ToArray());
        await peer.Disposed.Task.WaitAsync(TestContext.Current.CancellationToken);
        while (!connection.Completion.IsCompleted)
        {
            coordinator.Tick();
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }

    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task HeldMovementRepeatsWithRealDelayedWebSocketAndStopsAfterRelease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new TestGameServer(IPAddress.Loopback, useWallClock: true);
        server.StartTicks();
        await using var held = server.ConnectWebSocket(delayed: true);
        await using var observer = server.ConnectWebSocket(delayed: false);
        var updates = new ConcurrentQueue<GameSnapshot>();
        using var echo = held.On<string, string>("LatencyProbe", nonce => nonce);
        using var subscription = observer.On<GameSnapshot>("Snapshot", updates.Enqueue);
        await held.StartAsync(cancellationToken).WaitAsync(Deadline, cancellationToken);
        var player = await Join(held, "Held");
        await observer.StartAsync(cancellationToken).WaitAsync(Deadline, cancellationToken);
        await Join(observer, "Observer");
        await SnapshotWith(updates, state => state.Arena.Phase != Phase.Waiting);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        long sequence = 0;
        while (clock.Elapsed < TimeSpan.FromMilliseconds(1150))
        {
            await held.SendAsync("SetHeld", Direction.Up, ++sequence, cancellationToken);
            await Task.Delay(100, cancellationToken);
        }
        await held.SendAsync("SetHeld", null, ++sequence, cancellationToken);

        PlayerState Sample(GameSnapshot state) => state.Players.Single(candidate => candidate.PlayerId == player.PlayerId);
        var released = await SnapshotWith(updates, state =>
            state.Players.Length == 2 && Sample(state).LastProcessedSequence == sequence);
        Assert.InRange(Sample(released).RecentPushCount, 8, 12);
        Assert.InRange(Assert.IsType<int>(Sample(released).LatencyMilliseconds), 250, 2000);
        var later = await SnapshotWith(updates, state => state.Arena.Tick >= released.Arena.Tick + 24);
        Assert.Equal(Sample(released).RecentPushCount, Sample(later).RecentPushCount);
        Assert.Equal(Sample(released).LastUpTick, Sample(later).LastUpTick);
    }


    [Fact]
    public async Task ServerPublishesMeasuredRoundTripToAnotherRealClient()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var server = new TestGameServer(IPAddress.Loopback);
        server.StartTicks();
        await using var measured = server.Connect();
        await using var observer = server.Connect();
        var updates = new ConcurrentQueue<GameSnapshot>();
        using var echo = measured.On<string, string>("LatencyProbe", nonce =>
        {
            server.Time.Advance(TimeSpan.FromMilliseconds(42));
            return nonce;
        });
        using var subscription = observer.On<GameSnapshot>("Snapshot", updates.Enqueue);
        await measured.StartAsync(cancellationToken).WaitAsync(Deadline, cancellationToken);
        var player = await Join(measured, "Measured");
        await observer.StartAsync(cancellationToken).WaitAsync(Deadline, cancellationToken);
        var other = await Join(observer, "Observer");
        var snapshot = await SnapshotWith(updates, state => state.Players.Any(candidate =>
            candidate.PlayerId == player.PlayerId && candidate.LatencyMilliseconds == 42));
        Assert.Null(snapshot.Players.Single(candidate => candidate.PlayerId == other.PlayerId).LatencyMilliseconds);
    }


    [Fact]
    public async Task RealClientsSharePaddlesAndReceiveOrderedAuthoritativeSnapshots()
    {
        await using var server = new TestGameServer(IPAddress.Loopback);
        server.StartTicks();
        await using var first = server.Connect();
        await using var second = server.Connect();
        await using var third = server.Connect();
        var observed = new ConcurrentQueue<GameSnapshot>();
        var firstUpdates = new ConcurrentQueue<GameSnapshot>();
        var secondUpdates = new ConcurrentQueue<GameSnapshot>();
        using var firstSubscription = first.On<GameSnapshot>("Snapshot", firstUpdates.Enqueue);
        using var secondSubscription = second.On<GameSnapshot>("Snapshot", secondUpdates.Enqueue);
        using var thirdSubscription = third.On<GameSnapshot>("Snapshot", observed.Enqueue);

        await first.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var alice = await Join(first, "Alice01234");
        await Push(first, Direction.Down, 1);
        Assert.Equal(Phase.Waiting, alice.Snapshot.Arena.Phase);
        await second.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var bob = await Join(second, "Bob");
        Assert.NotEqual(alice.Team, bob.Team);
        Assert.Equal("Alice01234", bob.Snapshot.Players.Single(player => player.PlayerId == alice.PlayerId).DisplayName);

        await third.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var carol = await Join(third, "Carol");
        var teammate = alice.Team == carol.Team ? first : second;
        var teammateId = alice.Team == carol.Team ? alice.PlayerId : bob.PlayerId;
        var firstSequence = alice.Team == carol.Team ? 2L : 1L;
        var before = await SnapshotWith(observed, snapshot => snapshot.Players.Length == 3 && snapshot.Arena.Phase != Phase.Waiting);
        await Push(teammate, Direction.Down, firstSequence);
        var afterFirst = await SnapshotWith(observed, snapshot =>
            Sequence(snapshot, teammateId) == firstSequence && Paddle(snapshot, carol.Team).Y > 15);
        await Push(third, Direction.Down, 1);
        var afterSecond = await SnapshotWith(observed, snapshot =>
            Sequence(snapshot, carol.PlayerId) == 1 && snapshot.Arena.Tick > afterFirst.Arena.Tick);

        Assert.True(Paddle(afterSecond, carol.Team).Y > Paddle(afterFirst, carol.Team).Y);
        Assert.Equal(15, Paddle(afterSecond, carol.Team == Team.Left ? Team.Right : Team.Left).Y);
        Assert.True(before.Arena.Tick < afterFirst.Arena.Tick);
        var ticksSeen = observed.Select(snapshot => snapshot.Arena.Tick).ToArray();
        Assert.True(ticksSeen.Zip(ticksSeen.Skip(1), (earlier, later) => earlier < later).All(increasing => increasing));

        var goal = await SnapshotWith(observed, snapshot => snapshot.Arena.LeftScore + snapshot.Arena.RightScore > 0);
        await SnapshotWith(firstUpdates, snapshot =>
            snapshot.Arena.LeftScore == goal.Arena.LeftScore && snapshot.Arena.RightScore == goal.Arena.RightScore);
        await SnapshotWith(secondUpdates, snapshot =>
            snapshot.Arena.LeftScore == goal.Arena.LeftScore && snapshot.Arena.RightScore == goal.Arena.RightScore);

        await third.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await second.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await SnapshotWith(firstUpdates, snapshot => snapshot.Players.Length == 1 && snapshot.Arena.Phase == Phase.Waiting);
    }

    [Fact]
    public async Task RealClientsObserveResumeErrorsExpiryAndProcessedSequences()
    {
        await using var server = new TestGameServer(IPAddress.Loopback);
        server.StartTicks();
        await using var first = server.Connect();
        await using var second = server.Connect();
        await using var probe = server.Connect();
        var observed = new ConcurrentQueue<GameSnapshot>();
        using var subscription = second.On<GameSnapshot>("Snapshot", observed.Enqueue);
        await first.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await probe.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);

        foreach (var invalidName in new[] { "", "Alice012345", " Alice", "Alice ", "A\t", "A!", "Al\u00EFce" })
            await Marker("INVALID_NAME", () => Join(first, invalidName));
        await Marker("INVALID_RESUME_TOKEN", () => Join(first, "Alice", "unknown"));
        var alice = await Join(first, "Alice");
        await Marker("ALREADY_JOINED", () => Join(first, "Again"));
        await Marker("SESSION_ALREADY_CONNECTED", () => Join(probe, "Other", alice.ResumeToken));
        await Marker("NOT_JOINED", () => Push(probe, Direction.Up, 1));
        await Marker("INVALID_SEQUENCE", () => Push(first, Direction.Up, 0));
        await Assert.ThrowsAsync<HubException>(() => first.InvokeAsync("Push", 0, 1L, TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken));

        await Join(second, "Bob");
        await SnapshotWith(observed, snapshot => snapshot.Arena.Phase != Phase.Waiting);
        for (var sequence = 1; sequence <= 10; sequence++)
            await Push(first, Direction.Up, sequence);
        var accepted = await SnapshotWith(observed, snapshot => Sequence(snapshot, alice.PlayerId) == 10);
        await Push(first, Direction.Down, 10);
        await Push(first, Direction.Down, 11);
        var ignored = await SnapshotWith(observed, snapshot => Sequence(snapshot, alice.PlayerId) == 11);
        Assert.True(Paddle(accepted, alice.Team).Velocity < 0);
        Assert.True(Paddle(ignored, alice.Team).Velocity < 0);
        Assert.True(Paddle(ignored, alice.Team).Y <= Paddle(accepted, alice.Team).Y);

        await first.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await SnapshotWith(observed, snapshot => snapshot.Players.All(player => player.PlayerId != alice.PlayerId));
        server.Time.Advance(TimeSpan.FromMilliseconds(59_999));
        var resumed = await Join(probe, "Ignored", alice.ResumeToken);
        Assert.Equal(alice.PlayerId, resumed.PlayerId);
        Assert.Equal(alice.Team, resumed.Team);
        Assert.Equal(11, resumed.LastProcessedSequence);
        Assert.Equal("Alice", resumed.Snapshot.Players.Single(player => player.PlayerId == alice.PlayerId).DisplayName);

        await probe.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await SnapshotWith(observed, snapshot => snapshot.Arena.Tick > resumed.Snapshot.Arena.Tick &&
            snapshot.Players.All(player => player.PlayerId != alice.PlayerId));
        server.Time.Advance(TimeSpan.FromSeconds(60));
        await first.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await Marker("EXPIRED_RESUME_TOKEN", () => Join(first, "Alice", alice.ResumeToken));
        Assert.NotEqual(alice.PlayerId, (await Join(first, "Alice")).PlayerId);
    }

    [Fact]
    public async Task RealConnectionOverCapacityClosesWithCapacityMarker()
    {
        await using var server = new TestGameServer(IPAddress.Loopback);
        var coordinator = server.Services.GetRequiredService<GameCoordinator>();
        for (var index = 0; index < 64; index++)
            coordinator.Admit(index.ToString());

        await using var client = server.Connect();
        var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Closed += exception =>
        {
            closed.TrySetResult(exception);
            return Task.CompletedTask;
        };
        await client.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var error = await closed.Task.WaitAsync(Deadline, TestContext.Current.CancellationToken);
        Assert.Contains("PONGSOLE_CAPACITY_REACHED", error?.Message);
    }

    [Fact]
    public async Task PrivateSignalRSessionKeepsCredentialsOutOfTraceLogs()
    {
        using var logs = new CapturedLogs();
        await using var server = new TestGameServer(IPAddress.Parse("192.0.2.4"), "Private", logs: logs);
        server.StartTicks();
        await using var client = server.Connect(https: true, code: "test-code");
        await client.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var player = await Join(client, "Player");
        await client.StopAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        await client.StartAsync(TestContext.Current.CancellationToken).WaitAsync(Deadline, TestContext.Current.CancellationToken);
        var restored = await Join(client, "Player", player.ResumeToken);
        Assert.Equal(player.PlayerId, restored.PlayerId);

        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, line => line.Contains(player.ResumeToken, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, line => line.Contains("test-code", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, line => line.Contains("/game?id=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Loopback", "127.0.0.1", false, null, null, false, HttpStatusCode.OK)]
    [InlineData("Loopback", "::ffff:127.0.0.1", false, null, null, false, HttpStatusCode.OK)]
    [InlineData("Loopback", "192.0.2.4", false, null, "127.0.0.1", false, HttpStatusCode.Forbidden)]
    [InlineData("Loopback", "192.0.2.4", false, null, "127.0.0.1", true, HttpStatusCode.Forbidden)]
    [InlineData("Private", "127.0.0.1", true, null, null, false, HttpStatusCode.Unauthorized)]
    [InlineData("Private", "127.0.0.1", true, "wrong", null, false, HttpStatusCode.Forbidden)]
    [InlineData("Private", "127.0.0.1", false, "test-code", null, false, HttpStatusCode.Forbidden)]
    [InlineData("Private", "192.0.2.4", true, "test-code", null, false, HttpStatusCode.OK)]
    [InlineData("Private", "192.0.2.4", false, "test-code", "192.0.2.5", false, HttpStatusCode.Forbidden)]
    [InlineData("Private", "192.0.2.4", false, "test-code", "192.0.2.5", true, HttpStatusCode.OK)]
    [InlineData("Private", "192.0.2.6", false, "test-code", "192.0.2.5", true, HttpStatusCode.Forbidden)]
    public async Task NegotiationRequiresAnAllowedPeerOrPrivateHttps(
        string mode, string peer, bool https, string? code, string? forwardedFor, bool knownProxy, HttpStatusCode expected)
    {
        await using var server = new TestGameServer(IPAddress.Parse(peer), mode, knownProxy);
        using var client = server.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = TestGameServer.Origin(https) });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/game/negotiate?negotiateVersion=1");
        if (code is not null)
            request.Headers.Add("X-Pong-Code", code);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
            request.Headers.Add("X-Forwarded-Proto", "https");
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task<JoinResult> Join(HubConnection client, string name, string? token = null) =>
        client.InvokeAsync<JoinResult>("Join", name, token).WaitAsync(Deadline, TestContext.Current.CancellationToken);

    private static Task Push(HubConnection client, Direction direction, long sequence) =>
        client.InvokeAsync("Push", direction, sequence).WaitAsync(Deadline, TestContext.Current.CancellationToken);

    private static async Task Marker(string marker, Func<Task> operation)
    {
        var error = await Assert.ThrowsAsync<HubException>(operation);
        Assert.Contains($"PONGSOLE_{marker}", error.Message);
    }

    private static long? Sequence(GameSnapshot snapshot, Guid id) =>
        snapshot.Players.SingleOrDefault(player => player.PlayerId == id)?.LastProcessedSequence;

    private static PaddleState Paddle(GameSnapshot snapshot, Team team) =>
        team == Team.Left ? snapshot.Arena.LeftPaddle : snapshot.Arena.RightPaddle;

    private static async Task<GameSnapshot> SnapshotWith(ConcurrentQueue<GameSnapshot> snapshots, Func<GameSnapshot, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Deadline);
        while (true)
        {
            if (snapshots.FirstOrDefault(predicate) is { } snapshot)
                return snapshot;
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class DelayedProbeSocket(WebSocket inner) : WebSocket
    {
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(150);
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;
        public override void Abort() => inner.Abort();
        public override void Dispose() => inner.Dispose();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => inner.CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken)
        {
            await Task.Delay(Delay, cancellationToken);
            await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var result = await inner.ReceiveAsync(buffer, cancellationToken);
            if (buffer.AsSpan(0, result.Count).IndexOf("LatencyProbe"u8) >= 0)
                await Task.Delay(Delay, cancellationToken);
            return result;
        }
    }


    private sealed class TestGameServer(IPAddress peer, string mode = "Loopback", bool knownProxy = false,
        CapturedLogs? logs = null, bool useWallClock = false, IRtcPeerFactory? relayFactory = null,
        ITurnCredentialProvider? relayCredentials = null)
        : WebApplicationFactory<Program>
    {
        internal FakeTimeProvider Time { get; } = new();

        internal static Uri Origin(bool https) => new(https ? "https://localhost" : "http://localhost");

        internal HubConnection Connect(bool https = false, string? code = null) => new HubConnectionBuilder()
            .WithUrl(new Uri(Origin(https), "/game"), options =>
            {
                if (code is not null)
                    options.Headers.Add("X-Pong-Code", code);
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => Server.CreateHandler();
            })
            .AddJsonProtocol(options => GameJson.Configure(options.PayloadSerializerOptions))
            .Build();

        internal HubConnection ConnectWebSocket(bool delayed) => new HubConnectionBuilder()
            .WithUrl(new Uri(Origin(false), "/game"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (context, cancellationToken) =>
                {
                    var socket = await Server.CreateWebSocketClient().ConnectAsync(context.Uri, cancellationToken);
                    return delayed ? new DelayedProbeSocket(socket) : socket;
                };
            })
            .AddJsonProtocol(options => GameJson.Configure(options.PayloadSerializerOptions))
            .Build();

        private TickPump? ticks;

        internal void StartTicks() => ticks = new TickPump(Services.GetRequiredService<GameCoordinator>(), useWallClock);

        public override async ValueTask DisposeAsync()
        {
            try
            {
                await base.DisposeAsync();
            }
            finally
            {
                if (ticks is not null)
                    await ticks.DisposeAsync();
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pong:Mode"] = mode,
                ["PONGSOLE_SHARED_CODE"] = "test-code",
                ["Pong:KnownProxies:0"] = knownProxy ? "192.0.2.4" : null
            }.Where(pair => pair.Value is not null)));
            if (logs is not null)
                builder.ConfigureLogging(logging => logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<GameEngine>();
                services.AddSingleton(new GameEngine(1));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(useWallClock ? TimeProvider.System : Time);
                services.AddSingleton<IStartupFilter>(new SetPeer(peer));
                if (relayFactory is null)
                    services.AddLegacyGameTransport();
                if (relayFactory is not null)
                {
                    services.RemoveAll<IRtcPeerFactory>();
                    services.AddSingleton(relayFactory);
                    services.RemoveAll<IDataProtectionProvider>();
                    services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                }
                if (relayCredentials is not null)
                {
                    services.RemoveAll<ITurnCredentialProvider>();
                    services.AddSingleton(relayCredentials);
                }
            });
        }

        private sealed class SetPeer(IPAddress peer) : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = peer;
                    await continuation(context);
                });
                next(app);
            };
        }
    }

    private sealed class TestTurnCredentials : ITurnCredentialProvider
    {
        public int Calls { get; private set; }

        public Task<TurnCredentials> CreateAsync(CancellationToken cancellationToken)
        {
            var id = ++Calls;
            return Task.FromResult(new TurnCredentials(
                [new TurnIceServer(["turn:turn.cloudflare.com:3478?transport=udp"], $"user-{id}", $"credential-{id}")],
                DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class TestRtcPeer : IRtcPeer, IRtcPeerFactory
    {
        internal const string Sdp = "v=0\r\no=- 1 2 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\n" +
            "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\nc=IN IP4 0.0.0.0\r\n" +
            "a=candidate:1 1 UDP 123 1.1.1.1 443 typ relay raddr 0.0.0.0 rport 0\r\n";

        internal int Offers { get; private set; }
        internal ImmutableArray<RtcIceServer> IceServers { get; private set; }
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public RtcPeerState State => RtcPeerState.Connected;
        public event Action<RtcPeerState>? StateChanged { add { } remove { } }
        public event Action<IRtcChannel>? ChannelReceived;

        internal void Open(IRtcChannel channel) => ChannelReceived?.Invoke(channel);

        public IRtcPeer Create(ImmutableArray<RtcIceServer> iceServers)
        {
            IceServers = iceServers;
            return this;
        }

        public Task<string> AcceptOfferAsync(string sdp, CancellationToken cancellationToken)
        {
            Offers++;
            return Task.FromResult(Sdp);
        }

        public IRtcChannel CreateChannel(string label, bool reliable) => throw new NotSupportedException();
        public Task<string> CreateOfferAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public void AcceptAnswer(string sdp) => throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestRtcChannel(string label) : IRtcChannel
    {
        public string Label => label;
        public bool IsOpen => true;
        public long BufferedAmount { get; set; }
        internal bool BufferOnSend { get; init; }
        public event Action? Opened { add { } remove { } }
        public event Action? Drained;
        public event Action? Closed { add { } remove { } }
        public event Action<ReadOnlyMemory<byte>>? Received;
        internal System.Threading.Channels.Channel<byte[]> Sent { get; } =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();

        public bool TrySend(ReadOnlySpan<byte> bytes)
        {
            if (BufferedAmount != 0)
                return false;
            Send(bytes.ToArray());
            return true;
        }

        public bool TrySendBatch(IReadOnlyList<byte[]> packets)
        {
            if (BufferedAmount != 0)
                return false;
            foreach (var packet in packets)
                Send(packet);
            return true;
        }

        private void Send(byte[] bytes)
        {
            Sent.Writer.TryWrite(bytes);
            if (BufferOnSend)
                BufferedAmount += bytes.Length;
        }
        internal void Receive(ReadOnlyMemory<byte> bytes) => Received?.Invoke(bytes);

        internal void Drain()
        {
            BufferedAmount = 0;
            Drained?.Invoke();
        }
    }

    private sealed class CapturedLogs : ILoggerProvider
    {
        internal ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(Messages);
        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class TickPump : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task loop;

        internal TickPump(GameCoordinator coordinator, bool useWallClock) =>
            loop = RunAsync(coordinator, useWallClock, cancellation.Token);

        private static async Task RunAsync(GameCoordinator coordinator, bool useWallClock, CancellationToken cancellation)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(useWallClock ? 1000.0 / 60 : 8));
            while (await timer.WaitForNextTickAsync(cancellation))
                coordinator.Tick();
        }

        public async ValueTask DisposeAsync()
        {
            await cancellation.CancelAsync();
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
            cancellation.Dispose();
        }
    }
}
