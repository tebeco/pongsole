using System.Diagnostics;

namespace Pongsole.Tunnel;

internal interface ITunnelLauncher
{
    Task<ITunnelChild> LaunchAsync(Uri origin, CancellationToken cancellation);
}

internal interface ITunnelChild : IAsyncDisposable
{
    bool IsRunning { get; }
    TextReader Output { get; }
    TextReader Error { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellation);
}

internal sealed class CloudflaredLauncher(CloudflaredInstaller installer) : ITunnelLauncher
{
    public async Task<ITunnelChild> LaunchAsync(Uri origin, CancellationToken cancellation)
    {
        var installation = await installer.PrepareAsync(cancellation);
        try
        {
            var config = Path.Combine(installation.DirectoryPath, "config.yml");
            await File.WriteAllTextAsync(config, "{}\n", cancellation);
            cancellation.ThrowIfCancellationRequested();
            var process = Process.Start(CreateStartInfo(installation.ExecutablePath, config, origin))
                ?? throw new InvalidOperationException("cloudflared did not start.");
            return new CloudflaredChild(process, installation);
        }
        catch
        {
            installation.Dispose();
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string config, Uri origin)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(config)!
        };
        foreach (var argument in new[] { "tunnel", "--config", config, "--no-autoupdate", "--url", origin.AbsoluteUri })
            start.ArgumentList.Add(argument);

        foreach (var key in start.Environment.Keys.ToArray())
        {
            if (key.Equals("PONGSOLE_SHARED_CODE", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("TUNNEL_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("CLOUDFLARED_", StringComparison.OrdinalIgnoreCase))
            {
                start.Environment.Remove(key);
            }
        }

        return start;
    }

    private sealed class CloudflaredChild(Process process, InstalledCloudflared installation) : ITunnelChild
    {
        public bool IsRunning => !process.HasExited;
        public TextReader Output => process.StandardOutput;
        public TextReader Error => process.StandardError;

        public async Task<int> WaitForExitAsync(CancellationToken cancellation)
        {
            await process.WaitForExitAsync(cancellation);
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) when (process.HasExited)
                    {
                    }
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
            finally
            {
                process.Dispose();
                installation.Dispose();
            }
        }
    }
}
