using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
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

namespace Pongsole.Server.Tests;

public sealed class GameServerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task RealClientsSharePaddlesAndReceiveOrderedAuthoritativeSnapshots()
    {
        await using var server = new TestGameServer(IPAddress.Loopback);
        using var ticks = server.StartTicks();
        await using var first = server.Connect();
        await using var second = server.Connect();
        await using var third = server.Connect();
        var observed = new ConcurrentQueue<GameSnapshot>();
        using var subscription = third.On<GameSnapshot>("Snapshot", observed.Enqueue);

        await first.StartAsync().WaitAsync(Deadline);
        var alice = await Join(first, " Alice ");
        await Push(first, Direction.Down, 1);
        Assert.Equal(Phase.Waiting, alice.Snapshot.Arena.Phase);
        await second.StartAsync().WaitAsync(Deadline);
        var bob = await Join(second, "Bob");
        Assert.NotEqual(alice.Team, bob.Team);
        Assert.Equal("Alice", bob.Snapshot.Players.Single(player => player.PlayerId == alice.PlayerId).DisplayName);

        await third.StartAsync().WaitAsync(Deadline);
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

        await third.StopAsync().WaitAsync(Deadline);
        await second.StopAsync().WaitAsync(Deadline);
        var firstUpdates = new ConcurrentQueue<GameSnapshot>();
        using var firstSubscription = first.On<GameSnapshot>("Snapshot", firstUpdates.Enqueue);
        await SnapshotWith(firstUpdates, snapshot => snapshot.Players.Length == 1 && snapshot.Arena.Phase == Phase.Waiting);
    }

    [Fact]
    public async Task RealClientsObserveResumeErrorsExpiryAndProcessedSequences()
    {
        await using var server = new TestGameServer(IPAddress.Loopback);
        using var ticks = server.StartTicks();
        await using var first = server.Connect();
        await using var second = server.Connect();
        await using var probe = server.Connect();
        var observed = new ConcurrentQueue<GameSnapshot>();
        using var subscription = second.On<GameSnapshot>("Snapshot", observed.Enqueue);
        await first.StartAsync().WaitAsync(Deadline);
        await second.StartAsync().WaitAsync(Deadline);
        await probe.StartAsync().WaitAsync(Deadline);

        await Marker("INVALID_NAME", () => Join(first, "A\t"));
        await Marker("INVALID_RESUME_TOKEN", () => Join(first, "Alice", "unknown"));
        var alice = await Join(first, "Alice");
        await Marker("ALREADY_JOINED", () => Join(first, "Again"));
        await Marker("SESSION_ALREADY_CONNECTED", () => Join(probe, "Other", alice.ResumeToken));
        await Marker("NOT_JOINED", () => Push(probe, Direction.Up, 1));
        await Marker("INVALID_SEQUENCE", () => Push(first, Direction.Up, 0));

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

        await first.StopAsync().WaitAsync(Deadline);
        await SnapshotWith(observed, snapshot => snapshot.Players.All(player => player.PlayerId != alice.PlayerId));
        server.Time.Advance(TimeSpan.FromMilliseconds(59_999));
        var resumed = await Join(probe, "Ignored", alice.ResumeToken);
        Assert.Equal(alice.PlayerId, resumed.PlayerId);
        Assert.Equal(alice.Team, resumed.Team);
        Assert.Equal(11, resumed.LastProcessedSequence);
        Assert.Equal("Alice", resumed.Snapshot.Players.Single(player => player.PlayerId == alice.PlayerId).DisplayName);

        await probe.StopAsync().WaitAsync(Deadline);
        await SnapshotWith(observed, snapshot => snapshot.Arena.Tick > resumed.Snapshot.Arena.Tick &&
            snapshot.Players.All(player => player.PlayerId != alice.PlayerId));
        server.Time.Advance(TimeSpan.FromSeconds(60));
        await first.StartAsync().WaitAsync(Deadline);
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
        await client.StartAsync().WaitAsync(Deadline);
        var error = await closed.Task.WaitAsync(Deadline);
        Assert.Contains("PONGSOLE_CAPACITY_REACHED", error?.Message);
    }

    [Fact]
    public async Task PrivateSignalRSessionKeepsCredentialsOutOfTraceLogs()
    {
        using var logs = new CapturedLogs();
        await using var server = new TestGameServer(IPAddress.Parse("192.0.2.4"), "Private", logs: logs);
        using var ticks = server.StartTicks();
        await using var client = server.Connect(https: true, code: "test-code");
        await client.StartAsync().WaitAsync(Deadline);
        var player = await Join(client, "Player");
        await client.StopAsync().WaitAsync(Deadline);
        await client.StartAsync().WaitAsync(Deadline);
        var restored = await Join(client, "Player", player.ResumeToken);
        Assert.Equal(player.PlayerId, restored.PlayerId);

        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, line => line.Contains(player.ResumeToken, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Messages, line => line.Contains("test-code", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Loopback", "127.0.0.1", false, null, null, false, HttpStatusCode.OK)]
    [InlineData("Loopback", "::ffff:127.0.0.1", false, null, null, false, HttpStatusCode.OK)]
    [InlineData("Loopback", "192.0.2.4", false, null, "127.0.0.1", false, HttpStatusCode.Forbidden)]
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

        using var response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    private static Task<JoinResult> Join(HubConnection client, string name, string? token = null) =>
        client.InvokeAsync<JoinResult>("Join", name, token).WaitAsync(Deadline);

    private static Task Push(HubConnection client, Direction direction, long sequence) =>
        client.InvokeAsync("Push", direction, sequence).WaitAsync(Deadline);

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
        using var timeout = new CancellationTokenSource(Deadline);
        while (true)
        {
            if (snapshots.FirstOrDefault(predicate) is { } snapshot)
                return snapshot;
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed class TestGameServer(IPAddress peer, string mode = "Loopback", bool knownProxy = false,
        CapturedLogs? logs = null)
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

        internal TickPump StartTicks() => new(Services.GetRequiredService<GameCoordinator>());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pong:Mode"] = mode,
                ["PONGSOLE_SHARED_CODE"] = "test-code",
                ["Pong:KnownProxies:0"] = knownProxy ? "192.0.2.4" : null
            }.Where(pair => pair.Value is not null)));
            if (logs is not null)
                builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Time);
                services.AddSingleton<IStartupFilter>(new SetPeer(peer));
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

    private sealed class TickPump : IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task loop;

        internal TickPump(GameCoordinator coordinator) => loop = RunAsync(coordinator, cancellation.Token);

        private static async Task RunAsync(GameCoordinator coordinator, CancellationToken cancellation)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(12));
            while (await timer.WaitForNextTickAsync(cancellation))
                coordinator.Tick();
        }

        public void Dispose()
        {
            cancellation.Cancel();
            try
            {
                loop.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            cancellation.Dispose();
        }
    }
}
