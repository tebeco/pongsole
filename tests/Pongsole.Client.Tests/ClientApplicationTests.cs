using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;

namespace Pongsole.Client.Tests;

public sealed class ClientApplicationTests
{
    private static readonly ClientOptions Loopback = new(new Uri("http://127.0.0.1:5187/game"), "Player");
    private static readonly Guid PlayerId = Guid.NewGuid();

    [Fact]
    public async Task ReconnectDiscardsMovementAndDoesNotReplayFailedSend()
    {
        var first = new TestConnection
        {
            Join = (_, _, _) => Task.FromResult(Result(PlayerId, 6, 700)),
            Push = (_, _, _) => Task.FromException(new IOException())
        };
        var second = new TestConnection { Join = (_, _, _) => Task.FromResult(Result(PlayerId, 6, 0)) };
        var terminal = new TestTerminal();
        var connects = 0;
        terminal.OnDraw = lines =>
        {
            if (lines.Any(line => line.Contains("Connecting") || line.Contains("Reconnecting")))
            {
                terminal.Enqueue(ConsoleKey.W);
            }
            else if (lines.Contains("Connected"))
            {
                terminal.Enqueue(connects == 1 ? ConsoleKey.S : second.Pushes.Count == 0 ? ConsoleKey.UpArrow : ConsoleKey.Q);
            }
        };

        await RunAsync(terminal, (_, _, _) => ++connects == 1 ? first : second);

        Assert.Equal(new string?[] { null }, first.Tokens);
        Assert.Equal(new[] { "resume" }, second.Tokens);
        Assert.Equal((Direction.Down, 7L), Assert.Single(first.Pushes));
        Assert.Equal((Direction.Up, 8L), Assert.Single(second.Pushes));
        Assert.True(first.Disposed && second.Disposed && terminal.Restored);
    }

    [Theory]
    [InlineData("PONGSOLE_INVALID_RESUME_TOKEN")]
    [InlineData("PONGSOLE_EXPIRED_RESUME_TOKEN")]
    public async Task RejectedResumeMakesOneFreshJoinWithNewBaselineAndSequence(string marker)
    {
        var first = new TestConnection { Join = (_, _, _) => Task.FromResult(Result(PlayerId, 60, 700)) };
        var nextId = Guid.NewGuid();
        var second = new TestConnection
        {
            Join = (_, token, _) => token is not null
                ? Task.FromException<JoinResult>(new HubException($"Invocation failed: HubException: {marker}."))
                : Task.FromResult(Result(nextId, 0, 1))
        };
        var terminal = new TestTerminal();
        var connects = 0;
        terminal.OnDraw = lines =>
        {
            if (!lines.Contains("Connected"))
            {
                return;
            }

            if (connects == 1)
            {
                first.IsConnected = false;
            }
            else
            {
                terminal.Enqueue(second.Pushes.Count == 0 ? ConsoleKey.W : ConsoleKey.Escape);
            }
        };

        await RunAsync(terminal, (_, _, _) => ++connects == 1 ? first : second);

        Assert.Equal(new string?[] { "resume", null }, second.Tokens);
        Assert.Equal((Direction.Up, 1L), Assert.Single(second.Pushes));
        Assert.True(terminal.Restored);
    }

    [Fact]
    public async Task MissingCodeCanBePromptedOnceAndIsMasked()
    {
        const string secret = "hidden";
        var unauthorized = new TestConnection
        {
            Start = _ => Task.FromException(new HttpRequestException("private error", null, HttpStatusCode.Unauthorized))
        };
        var game = new TestConnection();
        var codes = new List<string?>();
        var terminal = new TestTerminal();
        terminal.OnDraw = lines =>
        {
            if (lines.Contains("Shared code"))
            {
                if (lines.Contains("> " + new string('*', secret.Length)))
                {
                    terminal.Enqueue(ConsoleKey.Enter);
                }
                else
                {
                    terminal.EnqueueText(secret);
                }
            }
            else if (lines.Contains("Connected"))
            {
                terminal.Enqueue(ConsoleKey.Q);
            }
        };

        await RunAsync(terminal, (_, code, _) =>
        {
            codes.Add(code);
            return codes.Count == 1 ? unauthorized : game;
        });

        Assert.Equal(new string?[] { null, secret }, codes);
        Assert.DoesNotContain(terminal.Frames.SelectMany(frame => frame), line => line.Contains(secret));
        Assert.True(terminal.Restored && unauthorized.Disposed && game.Disposed);
    }

    [Fact]
    public async Task PromptsForMissingOptionsAndSuppliesEnvironmentCodeWithoutPrompt()
    {
        var terminal = new TestTerminal();
        var game = new TestConnection();
        terminal.OnDraw = lines =>
        {
            if (lines.Contains("Server URL"))
            {
                terminal.EnqueueText("https://example.test/");
                terminal.Enqueue(ConsoleKey.Enter);
            }
            else if (lines.Contains("Display name"))
            {
                terminal.EnqueueText("Player Two");
                terminal.Enqueue(ConsoleKey.Enter);
            }
            else if (lines.Contains("Connected"))
            {
                terminal.Enqueue(ConsoleKey.Q);
            }
        };

        await RunAsync(terminal, (endpoint, code, _) =>
        {
            Assert.Equal(new Uri("https://example.test/game"), endpoint);
            Assert.Equal("env-code", code);
            return game;
        }, new ClientOptions(null, null), "env-code");

        Assert.Equal("Player Two", Assert.Single(game.Names));
        Assert.DoesNotContain(terminal.Frames.SelectMany(frame => frame), line => line.Contains("Shared code"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AccessFailureDoesNotRepeatConfiguredCredentialsOrShowTransportText(HttpStatusCode status)
    {
        var terminal = new TestTerminal();
        var connection = new TestConnection
        {
            Start = _ => Task.FromException(new HttpRequestException("secret value", null, status))
        };
        var attempts = 0;

        var error = await Assert.ThrowsAsync<ClientFailure>(() => RunAsync(terminal, (_, _, _) =>
        {
            attempts++;
            return connection;
        }, code: "secret value"));

        Assert.Equal(1, attempts);
        Assert.DoesNotContain("secret value", error.Message);
        Assert.True(terminal.Restored && connection.Disposed);
    }

    [Fact]
    public async Task ActiveSessionRejectionDoesNotTryAnotherJoin()
    {
        var terminal = new TestTerminal();
        var connection = new TestConnection
        {
            Join = (_, _, _) => Task.FromException<JoinResult>(new HubException("Wrapped: PONGSOLE_SESSION_ALREADY_CONNECTED."))
        };

        var error = await Assert.ThrowsAsync<ClientFailure>(() => RunAsync(terminal, (_, _, _) => connection));

        Assert.Contains("still connected", error.Message);
        Assert.Single(connection.Tokens);
        Assert.True(terminal.Restored && connection.Disposed);
    }

    [Fact]
    public async Task CancelsInFlightConnectionWhenLeavingAndRestoresTerminal()
    {
        var terminal = new TestTerminal();
        var token = CancellationToken.None;
        var connection = new TestConnection
        {
            Start = cancellationToken =>
            {
                token = cancellationToken;
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };

        var draws = 0;
        terminal.OnDraw = _ =>
        {
            if (++draws == 2)
            {
                terminal.Enqueue(ConsoleKey.Escape);
            }
        };

        await RunAsync(terminal, (_, _, _) => connection);

        Assert.True(token.IsCancellationRequested);
        Assert.True(terminal.Restored && connection.Disposed);
    }

    [Fact]
    public async Task RedirectedTerminalFailsBeforeOpeningScreenOrTransport()
    {
        var terminal = new TestTerminal { IsInteractive = false };

        var error = await Assert.ThrowsAsync<ClientFailure>(() =>
            RunAsync(terminal, (_, _, _) => throw new InvalidOperationException()));

        Assert.Contains("interactive terminal", error.Message);
        Assert.False(terminal.Restored);
        Assert.Empty(terminal.Frames);
    }

    [Theory]
    [InlineData("PONGSOLE_INVALID_RESUME_TOKEN")]
    [InlineData("PONGSOLE_EXPIRED_RESUME_TOKEN")]
    [InlineData("PONGSOLE_SESSION_ALREADY_CONNECTED")]
    [InlineData("PONGSOLE_ALREADY_JOINED")]
    [InlineData("PONGSOLE_INVALID_NAME")]
    [InlineData("PONGSOLE_INVALID_DIRECTION")]
    [InlineData("PONGSOLE_INVALID_SEQUENCE")]
    [InlineData("PONGSOLE_NOT_JOINED")]
    [InlineData("PONGSOLE_CAPACITY_REACHED")]
    [InlineData("PONGSOLE_INPUT_QUEUE_FULL")]
    [InlineData("PONGSOLE_SERVER_STOPPING")]
    public void RecognizesOnlyExactWrappedMarker(string marker)
    {
        Assert.Equal(marker, ClientError.Marker(new HubException($"SignalR invocation failed: {marker}.")));
        Assert.Null(ClientError.Marker(new HubException($"SignalR invocation failed: {marker}_EXTRA.")));
    }

    [Fact]
    public async Task DiscardsMovementWhileWaitingOrTooSmallAndDropsQueueFullWithoutResending()
    {
        var terminal = new TestTerminal();
        Action<GameSnapshot>? receive = null;
        var playing = Result(PlayerId, 0, 2);
        var waiting = playing with
        {
            Snapshot = playing.Snapshot with { Arena = playing.Snapshot.Arena with { Phase = Phase.Waiting, Tick = 1 } }
        };
        var connection = new TestConnection
        {
            Join = (_, _, _) => Task.FromResult(waiting),
            Push = (_, sequence, _) => sequence == 1
                ? Task.FromException(new HubException("PONGSOLE_INPUT_QUEUE_FULL"))
                : Task.CompletedTask
        };
        var frame = 0;
        terminal.OnDraw = lines =>
        {
            if (!lines.Contains("Connected"))
            {
                return;
            }

            switch (++frame)
            {
                case 1:
                    Assert.Contains("Waiting for players on both teams", lines);
                    terminal.Enqueue(ConsoleKey.W);
                    terminal.Size = (79, 24);
                    receive!(playing.Snapshot);
                    break;
                case 2:
                    Assert.StartsWith("Resize to 80x24", lines[0]);
                    terminal.Enqueue(ConsoleKey.W);
                    terminal.Size = (80, 24);
                    break;
                case 3:
                    terminal.Enqueue(ConsoleKey.S);
                    break;
                case 4:
                    terminal.Enqueue(ConsoleKey.W);
                    break;
                case 5:
                    terminal.Enqueue(ConsoleKey.Q);
                    break;
            }
        };

        await RunAsync(terminal, (_, _, snapshot) =>
        {
            receive = snapshot;
            return connection;
        });

        Assert.Equal(new[] { (Direction.Down, 1L), (Direction.Up, 2L) }, connection.Pushes);
    }

    [Fact]
    public async Task ServerCapacityAndTransportFailuresUseSpecifiedDelays()
    {
        var terminal = new TestTerminal();
        var clock = new FakeTimeProvider();
        var timestamps = new List<DateTimeOffset>();
        terminal.OnDraw = lines =>
        {
            if (lines.Contains("Connected"))
            {
                terminal.Enqueue(ConsoleKey.Q);
            }
        };

        await RunAsync(terminal, (_, _, _) =>
        {
            timestamps.Add(clock.GetUtcNow());
            return timestamps.Count switch
            {
                1 => new TestConnection
                {
                    Join = (_, _, _) => Task.FromException<JoinResult>(new HubException("PONGSOLE_CAPACITY_REACHED"))
                },
                <= 6 => new TestConnection { Start = _ => Task.FromException(new HttpRequestException()) },
                _ => new TestConnection()
            };
        }, clock: clock);

        var expected = new[] { 10, 0, 2, 5, 10, 10 };
        Assert.Equal(expected.Length + 1, timestamps.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            var delay = (timestamps[index + 1] - timestamps[index]).TotalSeconds;
            Assert.InRange(delay, expected[index], expected[index] + 0.5);
        }
    }

    [Fact]
    public async Task InvalidServerSideNameCanBeCorrectedInteractively()
    {
        var terminal = new TestTerminal();
        var rejected = new TestConnection
        {
            Join = (_, _, _) => Task.FromException<JoinResult>(new HubException("PONGSOLE_INVALID_NAME"))
        };
        var joined = new TestConnection();
        var attempts = 0;
        terminal.OnDraw = lines =>
        {
            if (lines.Any(line => line.StartsWith("The server rejected the display name")))
            {
                terminal.EnqueueText("Corrected");
                terminal.Enqueue(ConsoleKey.Enter);
            }
            else if (lines.Contains("Connected"))
            {
                terminal.Enqueue(ConsoleKey.Q);
            }
        };

        await RunAsync(terminal, (_, _, _) => ++attempts == 1 ? rejected : joined);

        Assert.Equal("Player", Assert.Single(rejected.Names));
        Assert.Equal("Corrected", Assert.Single(joined.Names));
    }


    [Fact]
    public async Task InvalidFlagNameIsCorrectedBeforeOpeningTheConnection()
    {
        var terminal = new TestTerminal();
        var connection = new TestConnection();
        terminal.OnDraw = lines =>
        {
            if (lines.Any(line => line.StartsWith("Enter a display name")))
            {
                terminal.EnqueueText("Corrected");
                terminal.Enqueue(ConsoleKey.Enter);
            }
            else if (lines.Contains("Connected"))
            {
                terminal.Enqueue(ConsoleKey.Q);
            }
        };

        var options = ClientOptions.Parse(["--server", "http://localhost:5187", "--name", "  "]);
        await RunAsync(terminal, (_, _, _) => connection, options);

        Assert.Equal("Corrected", Assert.Single(connection.Names));
    }

    [Fact]
    public async Task SendsTwoKeysFromTheSameFrameInOrder()
    {
        var connection = new TestConnection();
        var terminal = new TestTerminal();
        var frame = 0;
        terminal.OnDraw = lines =>
        {
            if (!lines.Contains("Connected"))
            {
                return;
            }

            if (++frame == 1)
            {
                terminal.Enqueue(ConsoleKey.W);
                terminal.Enqueue(ConsoleKey.S);
            }
            else if (connection.Pushes.Count == 2)
            {
                terminal.Enqueue(ConsoleKey.Q);
            }
        };

        await RunAsync(terminal, (_, _, _) => connection);

        Assert.Equal(new[] { (Direction.Up, 1L), (Direction.Down, 2L) }, connection.Pushes);
    }

    [Fact]
    public async Task QueuesConnectedKeysDuringAnInFlightPushAndSendsOnceInOrder()
    {
        var firstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestConnection
        {
            Push = (_, sequence, cancellationToken) => sequence == 1
                ? firstSend.Task.WaitAsync(cancellationToken)
                : Task.CompletedTask
        };
        var terminal = new TestTerminal();
        var frame = 0;
        terminal.OnDraw = lines =>
        {
            if (!lines.Contains("Connected"))
            {
                return;
            }

            switch (++frame)
            {
                case 1:
                    terminal.Enqueue(ConsoleKey.W);
                    break;
                case 2:
                    Assert.Single(connection.Pushes);
                    terminal.Enqueue(ConsoleKey.S);
                    terminal.Enqueue(ConsoleKey.W);
                    break;
                case 3:
                    Assert.Single(connection.Pushes);
                    firstSend.SetResult();
                    break;
                default:
                    if (connection.Pushes.Count == 3)
                    {
                        terminal.Enqueue(ConsoleKey.Q);
                    }

                    break;
            }
        };

        await RunAsync(terminal, (_, _, _) => connection);

        Assert.Equal(new[] { (Direction.Up, 1L), (Direction.Down, 2L), (Direction.Up, 3L) }, connection.Pushes);
    }

    [Fact]
    public async Task DisconnectDuringInFlightPushDropsUnsentInputAcrossResume()
    {
        var token = CancellationToken.None;
        var first = new TestConnection
        {
            Push = (_, _, cancellationToken) =>
            {
                token = cancellationToken;
                return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        };
        var resumed = new TestConnection();
        var terminal = new TestTerminal();
        var attempts = 0;
        var frame = 0;
        terminal.OnDraw = lines =>
        {
            if (!lines.Contains("Connected"))
            {
                return;
            }

            switch (++frame)
            {
                case 1:
                    terminal.Enqueue(ConsoleKey.W);
                    terminal.Enqueue(ConsoleKey.S);
                    break;
                case 2:
                    Assert.Single(first.Pushes);
                    first.IsConnected = false;
                    break;
                case 3:
                    Assert.Equal(2, attempts);
                    terminal.Enqueue(ConsoleKey.W);
                    break;
                case 4:
                    terminal.Enqueue(ConsoleKey.Q);
                    break;
            }
        };

        await RunAsync(terminal, (_, _, _) => ++attempts == 1 ? first : resumed);

        Assert.True(token.IsCancellationRequested);
        Assert.Equal((Direction.Up, 1L), Assert.Single(first.Pushes));
        Assert.Equal((Direction.Up, 2L), Assert.Single(resumed.Pushes));
        Assert.Equal("resume", Assert.Single(resumed.Tokens));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscardsQueuedMovementsWhenPlayBecomesUnavailable(bool resize)
    {
        var firstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new TestConnection
        {
            Push = (_, sequence, cancellationToken) => sequence == 1
                ? firstSend.Task.WaitAsync(cancellationToken)
                : Task.CompletedTask
        };
        var terminal = new TestTerminal();
        Action<GameSnapshot>? receive = null;
        var playing = Result(PlayerId, 0, 3).Snapshot;
        var waiting = playing with { Arena = playing.Arena with { Phase = Phase.Waiting, Tick = 2 } };
        var frame = 0;
        terminal.OnDraw = lines =>
        {
            if (!lines.Contains("Connected"))
            {
                return;
            }

            switch (++frame)
            {
                case 1:
                    terminal.Enqueue(ConsoleKey.W);
                    terminal.Enqueue(ConsoleKey.S);
                    break;
                case 2:
                    Assert.Single(connection.Pushes);
                    if (resize)
                    {
                        terminal.Size = (79, 24);
                    }
                    else
                    {
                        receive!(waiting);
                    }

                    break;
                case 3:
                    Assert.Single(connection.Pushes);
                    firstSend.SetResult();
                    if (resize)
                    {
                        Assert.StartsWith("Resize to 80x24", lines[0]);
                        terminal.Size = (80, 24);
                    }
                    else
                    {
                        Assert.Contains("Waiting for players on both teams", lines);
                        receive!(playing);
                    }

                    break;
                case 4:
                    terminal.Enqueue(ConsoleKey.W);
                    break;
                case 5:
                    terminal.Enqueue(ConsoleKey.Q);
                    break;
            }
        };

        await RunAsync(terminal, (_, _, snapshot) =>
        {
            receive = snapshot;
            return connection;
        });

        Assert.Equal(new[] { (Direction.Up, 1L), (Direction.Up, 2L) }, connection.Pushes);
    }


    private static async Task RunAsync(TestTerminal terminal,
        Func<Uri, string?, Action<GameSnapshot>, IGameConnection> connect,
        ClientOptions? options = null, string? code = null, FakeTimeProvider? clock = null)
    {
        clock ??= new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var app = new ClientApplication(terminal, clock, connect);
        var running = app.RunAsync(options ?? Loopback, code, cancellation.Token);
        while (!running.IsCompleted && !cancellation.IsCancellationRequested)
        {
            clock.Advance(TimeSpan.FromMilliseconds(34));
            await Task.Delay(1);
        }

        await running.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(cancellation.IsCancellationRequested);
    }

    private static JoinResult Result(Guid id, long sequence, long tick)
    {
        var player = new PlayerState(id, "Player", Team.Left, sequence);
        return new JoinResult(id, player.Team, "resume", sequence, SnapshotHistoryTests.Snapshot(tick, 50, [player]));
    }

    private sealed class TestConnection : IGameConnection
    {
        public bool IsConnected { get; set; } = true;
        public bool Disposed { get; private set; }
        public Func<CancellationToken, Task> Start { get; init; } = _ => Task.CompletedTask;
        public Func<string, string?, CancellationToken, Task<JoinResult>> Join { get; init; } =
            (_, _, _) => Task.FromResult(Result(PlayerId, 0, 1));
        public Func<Direction, long, CancellationToken, Task> Push { get; init; } = (_, _, _) => Task.CompletedTask;
        public List<string?> Tokens { get; } = [];
        public List<string> Names { get; } = [];
        public List<(Direction, long)> Pushes { get; } = [];

        public Task StartAsync(CancellationToken cancellationToken) => Start(cancellationToken);

        public Task<JoinResult> JoinAsync(string name, string? token, CancellationToken cancellationToken)
        {
            Names.Add(name);
            Tokens.Add(token);
            return Join(name, token, cancellationToken);
        }

        public Task PushAsync(Direction direction, long sequence, CancellationToken cancellationToken)
        {
            Pushes.Add((direction, sequence));
            return Push(direction, sequence, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestTerminal : ITerminal, IDisposable
    {
        private readonly Queue<ConsoleKeyInfo> keys = new();
        public bool IsInteractive { get; init; } = true;
        public bool Restored { get; private set; }
        public (int Width, int Height) Size { get; set; } = (80, 24);
        public List<IReadOnlyList<string>> Frames { get; } = [];
        public Action<IReadOnlyList<string>>? OnDraw { get; set; }

        public IDisposable Begin() => this;
        public void Dispose() => Restored = true;
        public ConsoleKeyInfo? ReadKey() => keys.TryDequeue(out var key) ? key : null;

        public void Draw(IReadOnlyList<string> lines)
        {
            Frames.Add(lines);
            OnDraw?.Invoke(lines);
        }

        public void Enqueue(ConsoleKey key) => keys.Enqueue(new ConsoleKeyInfo('\0', key, false, false, false));

        public void EnqueueText(string text)
        {
            foreach (var character in text)
            {
                keys.Enqueue(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false));
            }
        }
    }
}
