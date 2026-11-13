using System.Threading.Channels;
using Pongsole.Contracts;

namespace Pongsole.Server;

internal static class SnapshotSender
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync(ChannelReader<GameSnapshot> snapshots,
        Func<GameSnapshot, CancellationToken, Task> send, Action abort, TimeProvider time,
        CancellationToken connectionAborted)
    {
        try
        {
            await foreach (var snapshot in snapshots.ReadAllAsync(connectionAborted))
            {
                using var timeout = new CancellationTokenSource(SendTimeout, time);
                using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, connectionAborted);
                await send(snapshot, sendCancellation.Token).WaitAsync(sendCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (connectionAborted.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            abort();
        }
    }
}
