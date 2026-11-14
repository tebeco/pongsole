namespace Pongsole.Tunnel;

internal sealed class TunnelService(ITunnelLauncher launcher, Uri origin, TimeProvider time,
    IHostApplicationLifetime lifetime, TunnelExitStatus status, ILogger<TunnelService> logger, NamedTunnel? named = null)
    : BackgroundService
{
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(60);
    private readonly Lock gate = new();
    private ITunnelChild? child;
    private Uri? publicAddress;

    internal Uri? PublicAddress
    {
        get
        {
            lock (gate)
                return !lifetime.ApplicationStopping.IsCancellationRequested && child?.IsRunning == true ? publicAddress : null;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var running = await launcher.LaunchAsync(origin, stoppingToken);
            using var readers = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            using var discovery = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var found = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var connected = false;
            var configured = false;
            lock (gate)
                child = running;

            var readOutput = DrainAsync(running.Output, Observe, readers.Token);
            var readError = DrainAsync(running.Error, Observe, readers.Token);
            var exit = running.WaitForExitAsync(readers.Token);
            var timeout = Task.Delay(DiscoveryTimeout, time, discovery.Token);

            try
            {
                var drains = new List<Task> { readOutput, readError };
                var first = await WaitForMilestoneAsync(drains, found.Task, exit, timeout);

                if (first == timeout)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    throw new TimeoutException("cloudflared did not become ready before the startup deadline.");
                }

                await discovery.CancelAsync();
                await WaitForMilestoneAsync(drains, exit);
                var exitCode = await exit;
                stoppingToken.ThrowIfCancellationRequested();
                logger.LogError("cloudflared exited unexpectedly with code {ExitCode}. Restart the tunnel resource.", exitCode);
            }
            finally
            {
                lock (gate)
                {
                    publicAddress = null;
                    child = null;
                }

                await readers.CancelAsync();
                await ObserveReaderAsync(Task.WhenAll(readOutput, readError), readers.Token);
            }

            void Observe(string line)
            {
                Uri? address;
                lock (gate)
                {
                    if (child != running || !running.IsRunning || publicAddress is not null || stoppingToken.IsCancellationRequested)
                        return;

                    if (named?.Address is { } stableAddress)
                    {
                        connected |= line.Contains(" INF Registered tunnel connection ", StringComparison.Ordinal);
                        configured |= line.Contains(" INF Updated to new configuration ", StringComparison.Ordinal);
                        address = connected && configured ? stableAddress : null;
                    }
                    else
                    {
                        address = TunnelAddress.ReadPublicAddress(line);
                    }

                    if (address is null)
                        return;
                    publicAddress = address;
                }

                found.TrySetResult();
                logger.LogInformation("Tunnel public homepage is {PublicAddress}", address.AbsoluteUri);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            // Child output and exception text can include environmental details. Log only bounded diagnostics.
            logger.LogError("Tunnel failed ({FailureType}). Check the cloudflared configuration and network, then restart the resource.",
                exception.GetType().Name);
        }

        if (!stoppingToken.IsCancellationRequested && !lifetime.ApplicationStopping.IsCancellationRequested)
        {
            status.MarkFailed();
            lifetime.StopApplication();
        }
    }

    private static async Task<Task> WaitForMilestoneAsync(List<Task> drains, params Task[] milestones)
    {
        while (true)
        {
            var completed = await Task.WhenAny(milestones.Concat(drains));
            if (!drains.Remove(completed))
                return completed;

            await completed;
        }
    }

    private static async Task ObserveReaderAsync(Task reader, CancellationToken cancellation)
    {
        try
        {
            await reader;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    internal static async Task DrainAsync(TextReader reader, Action<string> observe, CancellationToken cancellation)
    {
        var chunk = new char[1024];
        var line = new char[4096];
        var length = 0;
        bool discard = false;
        int read;
        while ((read = await reader.ReadAsync(chunk, cancellation)) != 0)
        {
            foreach (var character in chunk.AsSpan(0, read))
            {
                if (character == '\n')
                {
                    if (!discard)
                        observe(new string(line, 0, length));
                    length = 0;
                    discard = false;
                }
                else if (length < line.Length)
                    line[length++] = character;
                else
                    discard = true;
            }
        }

        if (!discard && length > 0)
            observe(new string(line, 0, length));
    }
}
