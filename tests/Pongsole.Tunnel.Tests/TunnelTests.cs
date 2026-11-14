using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Pongsole.Tunnel;

namespace Pongsole.Tunnel.Tests;

public sealed class TunnelTests
{
    [Theory]
    [InlineData("http://localhost:5187")]
    [InlineData("http://127.0.0.1:5187/")]
    [InlineData("http://[::1]:5187")]
    public void AcceptsOnlyLocalHttpOrigins(string input) => Assert.True(TunnelAddress.ParseOrigin(input).IsLoopback);

    [Theory]
    [InlineData("https://localhost")]
    [InlineData("http://localhost/?")]
    [InlineData("http://localhost/#")]
    [InlineData("http://localhost/@")]
    [InlineData("http://localhost:0")]
    [InlineData("http://a@localhost")]
    [InlineData("http://localhost.example")]
    [InlineData("http://192.0.2.1")]
    [InlineData("http://0.0.0.0")]
    public void RejectsUnusableOrigins(string input) => Assert.Throws<InvalidOperationException>(() => TunnelAddress.ParseOrigin(input));

    [Theory]
    [InlineData("https://ab-c.trycloudflare.com")]
    [InlineData("2026-10-30T17:11:00Z INF | https://ab-c.trycloudflare.com:443/ |")]
    public void FindsOnlyNormalizedPublicHomepage(string line) =>
        Assert.Equal("https://ab-c.trycloudflare.com/", TunnelAddress.ReadPublicAddress(line)?.AbsoluteUri);

    [Theory]
    [InlineData("http://ab-c.trycloudflare.com")]
    [InlineData("https://trycloudflare.com")]
    [InlineData("https://a.b.trycloudflare.com")]
    [InlineData("https://a.trycloudflare.com.evil.test")]
    [InlineData("https://a.trycloudflare.com:444")]
    [InlineData("https://a.trycloudflare.com/app")]
    [InlineData("https://a.trycloudflare.com/?")]
    [InlineData("https://a.trycloudflare.com/#")]
    [InlineData("https://a.trycloudflare.com@elsewhere.test")]
    [InlineData("https://name@a.trycloudflare.com")]
    [InlineData("https://-ab.trycloudflare.com")]
    [InlineData("https://ab-.trycloudflare.com")]
    [InlineData("random text https://ab.trycloudflare.com")]
    public void RejectsUnexpectedPublicUrl(string line) => Assert.Null(TunnelAddress.ReadPublicAddress(line));

    [Theory]
    [InlineData("WINDOWS", Architecture.X64, "cloudflared-windows-amd64.exe")]
    [InlineData("LINUX", Architecture.X64, "cloudflared-linux-amd64")]
    [InlineData("LINUX", Architecture.Arm64, "cloudflared-linux-arm64")]
    [InlineData("OSX", Architecture.X64, "cloudflared-darwin-amd64.tgz")]
    [InlineData("OSX", Architecture.Arm64, "cloudflared-darwin-arm64.tgz")]
    public void SelectsPinnedPlatformRelease(string os, Architecture architecture, string asset)
    {
        var release = CloudflaredRelease.Select(OSPlatform.Create(os), architecture);
        Assert.Equal(asset, release.Asset);
        Assert.Equal(64, release.Sha256.Length);
        Assert.Equal($"https://github.com/cloudflare/cloudflared/releases/download/2026.10.7/{asset}", release.DownloadUrl.AbsoluteUri);
    }

    [Fact]
    public void RejectsUnsupportedPlatform() =>
        Assert.Throws<PlatformNotSupportedException>(() => CloudflaredRelease.Select(OSPlatform.Windows, Architecture.Arm64));

    [Fact]
    public async Task DownloadsOnceAndRejectsCorruptCacheWithoutExecutingIt()
    {
        using var folder = new TemporaryFolder();
        var content = "test binary"u8.ToArray();
        var release = FakeRelease(content);
        using var handler = new Download(content);
        using var client = new HttpClient(handler);
        var installer = new CloudflaredInstaller(client);
        using var first = await installer.PrepareAsync(release, folder.Path, TestContext.Current.CancellationToken);
        using var second = await installer.PrepareAsync(release, folder.Path, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.ExecutablePath, second.ExecutablePath);
        Assert.Equal(content, await File.ReadAllBytesAsync(second.ExecutablePath, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);

        await File.WriteAllTextAsync(System.IO.Path.Combine(folder.Path, release.Asset), "corrupt", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            installer.PrepareAsync(release, folder.Path, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task WrongDownloadNeverEntersCache()
    {
        using var folder = new TemporaryFolder();
        var release = FakeRelease("expected"u8.ToArray());
        using var handler = new Download("wrong"u8.ToArray());
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new CloudflaredInstaller(client).PrepareAsync(release, folder.Path, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(System.IO.Path.Combine(folder.Path, release.Asset)));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.download"));
    }

    [Theory]
    [InlineData("cloudflared", TarEntryType.RegularFile, false, true)]
    [InlineData("../cloudflared", TarEntryType.RegularFile, false, false)]
    [InlineData("cloudflared", TarEntryType.SymbolicLink, false, false)]
    [InlineData("cloudflared", TarEntryType.RegularFile, true, false)]
    public async Task ArchiveAllowsOnlyOneRegularExecutable(string name, TarEntryType type, bool extraEntry, bool valid)
    {
        using var folder = new TemporaryFolder();
        var executable = System.IO.Path.Combine(folder.Path, "binary");
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionMode.Compress, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(type, name);
            if (type == TarEntryType.RegularFile)
                entry.DataStream = new MemoryStream("binary"u8.ToArray());
            else
                entry.LinkName = "/somewhere";

            tar.WriteEntry(entry);
            if (extraEntry)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "extra"));
        }

        archive.Position = 0;
        var extract = () => CloudflaredInstaller.ExtractAsync(archive, executable, TestContext.Current.CancellationToken);
        if (valid)
        {
            await extract();
            Assert.Equal("binary", await File.ReadAllTextAsync(executable, TestContext.Current.CancellationToken));
        }
        else
            await Assert.ThrowsAsync<InvalidDataException>(extract);
    }

    [Fact]
    public void LaunchUsesSeparateArgumentsWithoutShell()
    {
        var config = System.IO.Path.GetFullPath("some folder/config.yml");
        var start = CloudflaredLauncher.CreateStartInfo("cloudflared", config, new Uri("http://localhost:5187"));
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(["tunnel", "--config", config, "--no-autoupdate", "--url", "http://localhost:5187/"], start.ArgumentList);
        Assert.DoesNotContain(start.Environment.Keys, key => key.Equals("PONGSOLE_SHARED_CODE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NamedTunnelPassesTheTokenOnlyInTheChildEnvironment()
    {
        var config = Path.GetFullPath("some folder/config.yml");
        var start = CloudflaredLauncher.CreateStartInfo("cloudflared", config, new Uri("http://localhost:5187"), "fixture-token");
        Assert.Equal(["tunnel", "--config", config, "--no-autoupdate", "--loglevel", "info", "run"], start.ArgumentList);
        Assert.Equal("fixture-token", start.Environment["TUNNEL_TOKEN"]);
    }

    [Fact]
    public async Task NamedTunnelPublishesConfiguredAddressAfterConnectingAndLoadingIngress()
    {
        using var lifetime = new TestLifetime();
        var child = new TestChild();
        var address = new Uri("https://pong.example.com/");
        using var service = new TunnelService(new TestLauncher(child), new Uri("http://localhost:5187"), TimeProvider.System,
            lifetime, new TunnelExitStatus(), NullLogger<TunnelService>.Instance, new NamedTunnel(address, "fixture-token"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await service.StartAsync(timeout.Token);
        Assert.Null(service.PublicAddress);
        await child.Errors.Writer.WriteAsync("2026-11-14T09:00:00Z INF Registered tunnel connection connIndex=0", timeout.Token);
        await child.Errors.Writer.WriteAsync("2026-11-14T09:00:01Z INF Updated to new configuration config={}", timeout.Token);
        while (service.PublicAddress is null)
            await Task.Delay(10, timeout.Token);
        Assert.Equal(address, service.PublicAddress);

        await service.StopAsync(timeout.Token);
        Assert.Null(service.PublicAddress);
    }

    [Fact]
    public async Task DrainDropsOversizedLineAndPreservesFollowingBanner()
    {
        var seen = new List<string>();
        await TunnelService.DrainAsync(new StringReader(new string('x', 5000) + "\n| https://a.trycloudflare.com |"),
            seen.Add, TestContext.Current.CancellationToken);
        Assert.Equal("| https://a.trycloudflare.com |", Assert.Single(seen));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RedirectAndHealthTrackOnlyTheLiveChild(int exitCode)
    {
        var child = new TestChild();
        var launcher = new TestLauncher(child);
        await using var app = CreateApp(launcher);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await AssertResponse(client, "/", HttpStatusCode.ServiceUnavailable);
        await AssertResponse(client, "/health", HttpStatusCode.ServiceUnavailable);
        await child.Errors.Writer.WriteAsync("| https://a.trycloudflare.com |", TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (app.Services.GetRequiredService<TunnelService>().PublicAddress is null)
            await Task.Delay(10, timeout.Token);

        await AssertResponse(client, "/", HttpStatusCode.Found, "https://a.trycloudflare.com/");
        await AssertResponse(client, "/health", HttpStatusCode.OK);
        var status = app.Services.GetRequiredService<TunnelExitStatus>();
        var tunnel = app.Services.GetRequiredService<TunnelService>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        child.Exited.SetResult(exitCode);
        await child.Disposed.Task.WaitAsync(timeout.Token);
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
            await Task.Delay(10, timeout.Token);
        Assert.Equal(1, status.Code);
        Assert.Null(tunnel.PublicAddress);
    }

    [Fact]
    public async Task StoppingAppDisposesItsChildWithoutWaitingForDiscovery()
    {
        var child = new TestChild();
        await using var app = CreateApp(new TestLauncher(child));
        using var client = app.CreateClient();
        await AssertResponse(client, "/", HttpStatusCode.ServiceUnavailable);
        var status = app.Services.GetRequiredService<TunnelExitStatus>();
        await app.DisposeAsync();
        Assert.True(child.Disposed.Task.IsCompletedSuccessfully);
        Assert.Equal(0, status.Code);
    }

    [Fact]
    public async Task LaunchFailureStopsHostWithFailureStatus()
    {
        using var lifetime = new TestLifetime();
        var status = new TunnelExitStatus();
        using var service = CreateService(new FailingLauncher(), TimeProvider.System, lifetime, status);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(1, status.Code);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.Null(service.PublicAddress);
    }

    [Fact]
    public async Task DiscoveryTimeoutDisposesChildBeforeStoppingHostWithFailureStatus()
    {
        using var lifetime = new TestLifetime();
        var status = new TunnelExitStatus();
        var clock = new DiscoveryClock();
        var child = new TestChild();
        using var service = CreateService(new TestLauncher(child), clock, lifetime, status);
        var disposedWhenStopping = false;
        using var onStop = lifetime.ApplicationStopping.Register(() => disposedWhenStopping = child.Disposed.Task.IsCompletedSuccessfully);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await clock.TimerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        clock.Clock.Advance(TimeSpan.FromSeconds(60));
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(disposedWhenStopping);
        Assert.Equal(1, status.Code);
        Assert.Null(service.PublicAddress);
    }

    private static TunnelService CreateService(ITunnelLauncher launcher, TimeProvider time,
        IHostApplicationLifetime lifetime, TunnelExitStatus status) =>
        new(launcher, new Uri("http://localhost:5187"), time, lifetime, status, NullLogger<TunnelService>.Instance);

    private static WebApplicationFactory<Program> CreateApp(TestLauncher launcher) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Uri>();
            services.AddSingleton(new Uri("http://localhost:5187"));
            services.RemoveAll<ITunnelLauncher>();
            services.AddSingleton<ITunnelLauncher>(launcher);
        }));

    private static async Task AssertResponse(HttpClient client, string path, HttpStatusCode expected, string? location = null)
    {
        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.AbsoluteUri);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static CloudflaredRelease FakeRelease(byte[] bytes) =>
        new("linux-x64", "fake-binary", Convert.ToHexString(SHA256.HashData(bytes)));

    private sealed class TemporaryFolder : IDisposable
    {
        internal string Path { get; } = Directory.CreateTempSubdirectory("pongsole-tunnel-tests-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class Download(byte[] content) : HttpMessageHandler
    {
        internal int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        }
    }

    private sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stopping.Cancel();
        public void Dispose() => stopping.Dispose();
    }

    private sealed class DiscoveryClock : TimeProvider
    {
        internal FakeTimeProvider Clock { get; } = new();
        internal TaskCompletionSource TimerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Clock.CreateTimer(callback, state, dueTime, period);
            TimerStarted.TrySetResult();
            return timer;
        }
    }

    private sealed class FailingLauncher : ITunnelLauncher
    {
        public Task<ITunnelChild> LaunchAsync(Uri origin, CancellationToken cancellation) =>
            Task.FromException<ITunnelChild>(new IOException("Test startup failure"));
    }

    private sealed class TestLauncher(TestChild child) : ITunnelLauncher
    {
        public Task<ITunnelChild> LaunchAsync(Uri origin, CancellationToken cancellation) => Task.FromResult<ITunnelChild>(child);
    }

    private sealed class TestChild : ITunnelChild
    {
        internal Channel<string> Errors { get; } = Channel.CreateUnbounded<string>();
        internal TaskCompletionSource<int> Exited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsRunning => !Exited.Task.IsCompleted;
        public TextReader Output => TextReader.Null;
        public TextReader Error => new ChannelReader(Errors);
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => Exited.Task.WaitAsync(cancellation);
        public ValueTask DisposeAsync()
        {
            Exited.TrySetResult(0);
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ChannelReader(Channel<string> channel) : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var line = await channel.Reader.ReadAsync(cancellationToken) + "\n";
            line.AsMemory().CopyTo(buffer);
            return line.Length;
        }
    }
}
