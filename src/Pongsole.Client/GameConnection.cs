using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Pongsole.Contracts;

namespace Pongsole.Client;

internal interface IGameConnection : IAsyncDisposable
{
    bool IsConnected { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task<JoinResult> JoinAsync(string name, string? token, CancellationToken cancellationToken);
    Task PushAsync(Direction direction, long sequence, CancellationToken cancellationToken);
}

internal sealed class GameConnection : IGameConnection
{
    private readonly HubConnection connection;
    private readonly IDisposable snapshotSubscription;
    private readonly IDisposable latencySubscription;
    private readonly TaskCompletionSource<Exception?> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GameConnection(Uri endpoint, string? code, Action<GameSnapshot> onSnapshot)
    {
        connection = new HubConnectionBuilder()
            .WithUrl(endpoint, options =>
            {
                if (code is not null)
                {
                    options.Headers["X-Pong-Code"] = code;
                }

                options.HttpMessageHandlerFactory = handler =>
                {
                    if (handler is HttpClientHandler http)
                    {
                        http.AllowAutoRedirect = false;
                    }

                    return new OriginHandler(endpoint, handler);
                };
            })
            .AddJsonProtocol(options => GameJson.Configure(options.PayloadSerializerOptions))
            .Build();
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };
        snapshotSubscription = connection.On("Snapshot", onSnapshot);
        latencySubscription = connection.On<string, string>("LatencyProbe", nonce => nonce);
    }

    public bool IsConnected => connection.State == HubConnectionState.Connected && connection.ConnectionId is not null;

    public Task StartAsync(CancellationToken cancellationToken) => connection.StartAsync(cancellationToken);

    public Task<JoinResult> JoinAsync(string name, string? token, CancellationToken cancellationToken) =>
        InvokeAsync<JoinResult>("Join", [name, token], cancellationToken);

    public Task PushAsync(Direction direction, long sequence, CancellationToken cancellationToken) =>
        InvokeAsync<object?>("Push", [direction, sequence], cancellationToken);

    private async Task<T> InvokeAsync<T>(string method, object?[] arguments, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.InvokeCoreAsync<T>(method, arguments, cancellationToken);
        }
        catch (Exception error) when (!IsConnected && !cancellationToken.IsCancellationRequested)
        {
            // A server can reject admission and close before Join gets an invocation result.
            var marker = ClientError.Marker(error)
                ?? ClientError.Marker(await closed.Task.WaitAsync(cancellationToken));
            if (marker is not null)
            {
                throw new HubException(marker);
            }

            throw new IOException("The connection closed.", error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        connection.Closed += error =>
        {
            closed.TrySetResult(error);
            return Task.CompletedTask;
        };
        snapshotSubscription.Dispose();
        latencySubscription.Dispose();
        await connection.DisposeAsync();
    }

    private sealed class OriginHandler(Uri endpoint, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = request.RequestUri;
            if (target is null || target.Scheme != endpoint.Scheme || target.IdnHost != endpoint.IdnHost
                || target.Port != endpoint.Port)
            {
                throw new ClientFailure("The server redirected the game connection to a different origin.");
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
