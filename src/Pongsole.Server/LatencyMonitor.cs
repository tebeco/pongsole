using System.Security.Cryptography;

namespace Pongsole.Server;

internal static class LatencyMonitor
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    internal static async Task RunAsync(Func<string, CancellationToken, Task<string>> invoke,
        Action<int?, long> publish, TimeProvider time, CancellationToken disconnected)
    {
        Queue<int> samples = new(3);
        try
        {
            while (!disconnected.IsCancellationRequested)
            {
                var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                using var timeout = new CancellationTokenSource(Interval, time);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, disconnected);
                var started = time.GetTimestamp();
                var (echo, pending) = await ProbeAsync(invoke, nonce, cancellation.Token);
                var measuredAt = time.GetTimestamp();
                disconnected.ThrowIfCancellationRequested();

                var nextProbe = Task.Delay(Interval, time, disconnected);
                var elapsed = time.GetElapsedTime(started, measuredAt);
                if (echo != nonce || elapsed < TimeSpan.Zero || elapsed > Interval)
                {
                    samples.Clear();
                    publish(null, measuredAt);
                }
                else
                {
                    if (samples.Count == 3)
                        samples.Dequeue();
                    samples.Enqueue((int)Math.Ceiling(elapsed.TotalMilliseconds));
                    publish(samples.Order().ElementAt(samples.Count / 2), measuredAt);
                }

                await nextProbe;
                // An uncooperative invocation must not allow overlapping probes.
                if (pending is not null)
                    await pending.WaitAsync(disconnected).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        catch (OperationCanceledException) when (disconnected.IsCancellationRequested)
        {
        }
    }

    private static async Task<(string? Echo, Task? Pending)> ProbeAsync(
        Func<string, CancellationToken, Task<string>> invoke, string nonce, CancellationToken cancellationToken)
    {
        Task<string>? invocation = null;
        try
        {
            invocation = invoke(nonce, cancellationToken);
            return (await invocation.WaitAsync(cancellationToken), null);
        }
        catch (Exception)
        {
            if (invocation is not null)
            {
                // A transport that ignores cancellation may finish after the lease closes.
                _ = invocation.ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            return (null, invocation);
        }
    }
}
