using System.Net;
using Microsoft.AspNetCore.SignalR;
using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class ClientApplication(
    ITerminal terminal,
    TimeProvider clock,
    Func<Uri, string?, Action<GameSnapshot>, IGameConnection> connect)
{
    private const int MovementQueueCapacity = 10;
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 30);
    private static readonly TimeSpan[] RetryDelays = [
        TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)
    ];
    private readonly TerminalPrompt prompt = new(terminal, clock);

    public async Task RunAsync(ClientOptions options, string? sharedCode, CancellationToken cancellationToken)
    {
        if (!terminal.IsInteractive)
        {
            throw new ClientFailure("Pongsole needs an interactive terminal with keyboard input and screen output.");
        }

        using var screen = terminal.Begin();
        try
        {
            var endpoint = options.Endpoint ?? await PromptUntilValidAsync("Server URL", ClientOptions.ParseEndpoint, cancellationToken);
            var name = await GetNameAsync(options.Name, cancellationToken);
            if (string.IsNullOrEmpty(sharedCode))
            {
                sharedCode = ClientOptions.IsLoopback(endpoint) ? null : await PromptForCodeAsync(cancellationToken);
            }

            await RunGameAsync(endpoint, name, sharedCode, cancellationToken);
        }
        catch (Exception error) when (error is ClientExit || (error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // Escape, Q or Ctrl+C ended the session.
        }
    }

    private async Task RunGameAsync(Uri endpoint, string name, string? code, CancellationToken cancellationToken)
    {
        var player = new PlayerSession();
        var lastView = new SnapshotHistory(clock);
        var retryIndex = 0;
        var status = "Connecting";
        var delay = TimeSpan.Zero;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitWithFramesAsync(Task.Delay(delay, clock, cancellationToken), lastView, player.Team, status, cancellationToken);
            var history = new SnapshotHistory(clock);

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await using var connection = connect(endpoint, code, history.Accept);
                var joining = JoinAsync(connection, player, name, attempt.Token);
                JoinResult join;

                try
                {
                    await WaitWithFramesAsync(joining, lastView, player.Team, status, attempt.Token);
                    join = await joining;
                }
                finally
                {
                    if (!joining.IsCompleted)
                    {
                        await attempt.CancelAsync();
                        await ObserveAsync(joining);
                    }
                }

                player.Join(join);
                history.EstablishBaseline(join);
                lastView = history;
                retryIndex = 0;
                DiscardKeys();
                await PlayAsync(connection, history, player, attempt.Token);
                status = "Reconnecting";
            }
            catch (HubException error)
            {
                var marker = ClientError.Marker(error);
                switch (marker)
                {
                    case "PONGSOLE_CAPACITY_REACHED":
                        delay = RetryDelays[^1];
                        status = "Server full. Retrying in 10 seconds";
                        continue;
                    case "PONGSOLE_SERVER_STOPPING":
                        status = "Server stopping. Reconnecting";
                        break;
                    case "PONGSOLE_INVALID_NAME":
                        name = await PromptUntilValidAsync("The server rejected the display name. Enter a new name",
                            ClientOptions.ParseName, cancellationToken);
                        delay = TimeSpan.Zero;
                        continue;
                    default:
                        throw new ClientFailure(ClientError.Explain(marker));
                }
            }
            catch (Exception error) when (ClientError.HttpStatus(error) is HttpStatusCode.Unauthorized)
            {
                if (code is not null)
                {
                    throw new ClientFailure("The server did not accept the shared code.");
                }

                code = await PromptForCodeAsync(cancellationToken);
                delay = TimeSpan.Zero;
                continue;
            }
            catch (Exception error) when (ClientError.HttpStatus(error) is HttpStatusCode.Forbidden)
            {
                throw new ClientFailure("The server denied access. Check the shared code and HTTPS URL.");
            }
            catch (Exception error) when (IsTransient(error, cancellationToken))
            {
                status = "Connection unavailable. Reconnecting";
            }

            delay = RetryDelays[retryIndex];
            retryIndex = Math.Min(retryIndex + 1, RetryDelays.Length - 1);
        }
    }

    private static async Task<JoinResult> JoinAsync(
        IGameConnection connection, PlayerSession player, string name, CancellationToken cancellationToken)
    {
        await connection.StartAsync(cancellationToken);
        try
        {
            return await connection.JoinAsync(name, player.Token, cancellationToken);
        }
        catch (HubException error) when (player.Token is not null
            && ClientError.Marker(error) is "PONGSOLE_INVALID_RESUME_TOKEN" or "PONGSOLE_EXPIRED_RESUME_TOKEN")
        {
            player.Clear();
            return await connection.JoinAsync(name, null, cancellationToken);
        }
    }

    private async Task PlayAsync(
        IGameConnection connection, SnapshotHistory history, PlayerSession player, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FrameInterval, clock);
        using var sending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var movements = new Queue<Direction>(MovementQueueCapacity);
        Task? pending = null;

        try
        {
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pending is { IsCompleted: true })
                {
                    try
                    {
                        await pending;
                    }
                    catch (HubException error) when (ClientError.Marker(error) == "PONGSOLE_INPUT_QUEUE_FULL")
                    {
                        // The sequence was sent once. Subsequent movements use new sequences.
                    }
                    finally
                    {
                        pending = null;
                    }
                }

                var view = history.Read();
                player.Reconcile(view.LastProcessed);
                if (!connection.IsConnected)
                {
                    return;
                }

                var (width, height) = terminal.Size;
                var canMove = GameRenderer.Fits(width, height) && view.Arena?.Phase is Phase.Serving or Phase.Playing;
                terminal.Draw(GameRenderer.Draw(width, height, view.Arena, view.PlayerCount, player.Team, "Connected"));
                if (!canMove)
                {
                    movements.Clear();
                }

                while (terminal.ReadKey() is { } key)
                {
                    ThrowIfExit(key);
                    if (canMove && movements.Count < MovementQueueCapacity && connection.IsConnected
                        && GameInput.Movement(key.Key) is { } direction)
                    {
                        movements.Enqueue(direction);
                    }
                }

                if (pending is null && connection.IsConnected && movements.TryDequeue(out var next))
                {
                    pending = SendAsync(connection, player, history, next, sending.Token);
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        finally
        {
            await sending.CancelAsync();
            if (pending is not null)
            {
                await ObserveAsync(pending);
            }
        }
    }

    private static async Task SendAsync(IGameConnection connection, PlayerSession player, SnapshotHistory history,
        Direction direction, CancellationToken cancellationToken)
    {
        var sequence = player.NextSequence(history.Read().LastProcessed);
        await connection.PushAsync(direction, sequence, cancellationToken);
    }

    private async Task WaitWithFramesAsync(Task task, SnapshotHistory history, Team? team,
        string status, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(FrameInterval, clock);
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            DiscardKeys();
            var view = history.Read();
            var (width, height) = terminal.Size;
            terminal.Draw(GameRenderer.Draw(width, height, view.Arena, view.PlayerCount, team, status));
            if (task.IsCompleted)
            {
                break;
            }

            await timer.WaitForNextTickAsync(cancellationToken);
        }
        while (true);

        await task;
    }

    private void DiscardKeys()
    {
        while (terminal.ReadKey() is { } key)
        {
            ThrowIfExit(key);
        }
    }

    private static void ThrowIfExit(ConsoleKeyInfo key)
    {
        if (GameInput.IsExit(key.Key))
        {
            throw new ClientExit();
        }
    }

    private async Task<string> GetNameAsync(string? name, CancellationToken cancellationToken)
    {
        var label = "Display name";
        if (name is not null)
        {
            try
            {
                return ClientOptions.ParseName(name);
            }
            catch (ArgumentException error)
            {
                label = error.Message;
            }
        }

        return await PromptUntilValidAsync(label, ClientOptions.ParseName, cancellationToken);
    }

    private async Task<T> PromptUntilValidAsync<T>(string label, Func<string, T> parse, CancellationToken cancellationToken)
    {
        while (true)
        {
            var value = await prompt.ReadAsync(label, masked: false, cancellationToken);
            try
            {
                return parse(value);
            }
            catch (ArgumentException error)
            {
                label = error.Message;
            }
        }
    }

    private async Task<string> PromptForCodeAsync(CancellationToken cancellationToken)
    {
        var label = "Shared code";
        while (true)
        {
            var value = await prompt.ReadAsync(label, masked: true, cancellationToken);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }

            label = "Enter the shared code";
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // Shutdown already superseded this operation.
        }
    }

    private static bool IsTransient(Exception error, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && error is HttpRequestException or IOException or TimeoutException or OperationCanceledException
            or System.Net.WebSockets.WebSocketException;
}

internal sealed class ClientFailure(string message) : Exception(message);

internal sealed class ClientExit : Exception;
