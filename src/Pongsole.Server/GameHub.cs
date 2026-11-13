using Microsoft.AspNetCore.SignalR;
using Pongsole.Contracts;

namespace Pongsole.Server;

internal sealed class GameHub(GameCoordinator coordinator, IHubContext<GameHub> hub, TimeProvider time) : Hub
{
    private static readonly object LeaseKey = new();
    private GameCoordinator.ConnectionLease Lease => (GameCoordinator.ConnectionLease)Context.Items[LeaseKey]!;

    public Task<JoinResult> Join(string displayName, string? resumeToken) => coordinator.JoinAsync(Lease, displayName, resumeToken);

    public Task Push(Direction direction, long sequence) => coordinator.PushAsync(Lease, direction, sequence);

    public Task SetHeld(Direction? direction, long sequence) => coordinator.SetHeldAsync(Lease, direction, sequence);

    public override Task OnConnectedAsync()
    {
        var lease = coordinator.Admit(Context.ConnectionId);
        Context.Items[LeaseKey] = lease;
        var client = hub.Clients.Client(Context.ConnectionId);
        _ = SnapshotSender.RunAsync(lease.Snapshots.Reader,
            (snapshot, cancellationToken) => client.SendAsync("Snapshot", snapshot, cancellationToken),
            Context.Abort, time, Context.ConnectionAborted);
        var latencyCoordinator = coordinator;
        _ = LatencyMonitor.RunAsync(
            (nonce, token) => client.InvokeAsync<string>("LatencyProbe", nonce, token),
            (milliseconds, measuredAt) => latencyCoordinator.UpdateLatency(lease, milliseconds, measuredAt),
            time, Context.ConnectionAborted);
        return Task.CompletedTask;
    }

    public override Task OnDisconnectedAsync(Exception? exception) =>
        Context.Items.TryGetValue(LeaseKey, out var lease)
            ? coordinator.DisconnectAsync((GameCoordinator.ConnectionLease)lease!)
            : Task.CompletedTask;
}
