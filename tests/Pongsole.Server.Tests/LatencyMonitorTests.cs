using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Pongsole.Server.Tests;

public sealed class LatencyMonitorTests
{
    [Fact]
    public async Task MonotonicSamplesUseMedianAndBadEchoClearsWindow()
    {
        var time = new FakeTimeProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var observations = Channel.CreateUnbounded<int?>();
        var nonces = new HashSet<string>();
        var results = new Queue<int>([10, 81, 31, -1, 6]);
        var monitor = LatencyMonitor.RunAsync((nonce, _) =>
        {
            Assert.Equal(32, nonce.Length);
            Assert.True(nonces.Add(nonce));
            var sample = results.Dequeue();
            time.Advance(TimeSpan.FromMilliseconds(Math.Max(sample, 0) + 0.1));
            return Task.FromResult(sample < 0 ? "wrong" : nonce);
        }, (sample, _) => observations.Writer.TryWrite(sample), time, stop.Token);

        try
        {
            foreach (var expected in new int?[] { 11, 82, 32, null, 7 })
            {
                Assert.Equal(expected, await observations.Reader.ReadAsync(stop.Token));
                if (results.Count > 0)
                    time.Advance(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await monitor;
        }
    }

    [Fact]
    public async Task TimeoutPublishesUnknownWithoutOverlappingUncooperativeProbe()
    {
        var time = new FakeTimeProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        TaskCompletionSource<string> pending = new();
        var observations = Channel.CreateUnbounded<int?>();
        var count = 0;
        var monitor = LatencyMonitor.RunAsync((_, _) =>
        {
            Interlocked.Increment(ref count);
            return pending.Task;
        }, (sample, _) => observations.Writer.TryWrite(sample), time, stop.Token);

        try
        {
            time.Advance(TimeSpan.FromSeconds(2));
            Assert.Null(await observations.Reader.ReadAsync(stop.Token));
            time.Advance(TimeSpan.FromSeconds(10));
            Assert.Equal(1, Volatile.Read(ref count));
        }
        finally
        {
            await stop.CancelAsync();
            await monitor;
            pending.SetResult("late");
        }
    }
}
