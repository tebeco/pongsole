using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace Pongsole.Server.Tests;

public sealed class CloudflareTurnCredentialProviderTests
{
    [Fact]
    public async Task IssuesShortLivedUdpCredentialsOnly()
    {
        var time = new FakeTimeProvider();
        var started = time.GetUtcNow();
        var token = TestContext.Current.CancellationToken;
        using var client = new HttpClient(new Handler(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://rtc.live.cloudflare.com/v1/turn/keys/test%2Fkey/credentials/generate-ice-servers",
                request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fixture-token", request.Headers.Authorization?.Parameter);
            Assert.Equal("""{"ttl":3600}""", await request.Content!.ReadAsStringAsync(cancellationToken));
            time.Advance(TimeSpan.FromSeconds(2));
            return Respond(HttpStatusCode.Created, """
                {"iceServers":[{"urls":["stun:stun.cloudflare.com:3478"]},
                    {"username":"fixture-user","credential":"fixture-password",
                     "urls":["turn:turn.cloudflare.com:3478?transport=udp",
                             "turn:turn.cloudflare.com:443?transport=udp",
                             "turn:turn.cloudflare.com:3478?transport=udp",
                             "turn:turn.cloudflare.com:3478?transport=tcp",
                             "turns:turn.cloudflare.com:443?transport=tcp",
                             "turn:unknown.test:3478?transport=udp"]}]}
                """);
        }));
        var result = await Create(client, time).CreateAsync(token);

        Assert.Equal(started.AddHours(1), result.ExpiresAt);
        var ice = Assert.Single(result.IceServers);
        Assert.Equal(["turn:turn.cloudflare.com:3478?transport=udp", "turn:turn.cloudflare.com:443?transport=udp"], ice.Urls);
        Assert.Equal("fixture-user", ice.Username);
        Assert.Equal("fixture-password", ice.Credential);
        Assert.DoesNotContain("fixture", ice.ToString());
        Assert.DoesNotContain("fixture", result.ToString());
    }

    [Theory]
    [InlineData(null, "fixture-token")]
    [InlineData("fixture-key", " ")]
    public async Task MissingConfigurationDoesNotSendRequest(string? key, string? apiToken)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new InvalidOperationException("unexpected request")));
        var provider = new CloudflareTurnCredentialProvider(client, new TurnOptions { KeyId = key, ApiToken = apiToken },
            new FakeTimeProvider());
        var error = await Assert.ThrowsAsync<TurnCredentialException>(() => provider.CreateAsync(TestContext.Current.CancellationToken));
        Assert.Equal("PONGSOLE_TURN_NOT_CONFIGURED", error.Message);
    }

    [Theory]
    [InlineData(302, "fixture-secret")]
    [InlineData(403, "fixture-secret")]
    [InlineData(200, "{}")]
    [InlineData(201, "malformed")]
    [InlineData(201, "[]")]
    [InlineData(201, """{"iceServers":[{"urls":["turn:turn.cloudflare.com:3478?transport=udp"],"username":"x"}]}""")]
    [InlineData(201, """{"iceServers":[{"urls":["stun:stun.cloudflare.com:3478"]}]}""")]
    public async Task InvalidResponsesFailSafelyWithoutRetry(int status, string body)
    {
        var attempts = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            attempts++;
            return Task.FromResult(Respond((HttpStatusCode)status, body));
        }));
        var error = await Assert.ThrowsAsync<TurnCredentialException>(() =>
            Create(client, new FakeTimeProvider()).CreateAsync(TestContext.Current.CancellationToken));
        Assert.Equal("PONGSOLE_TURN_UNAVAILABLE", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task BoundsResponseEvenWhenContentLengthIsAbsent()
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StreamContent(new UnseekableStream(new byte[64 * 1024 + 1]))
        })));
        var error = await Assert.ThrowsAsync<TurnCredentialException>(() =>
            Create(client, new FakeTimeProvider()).CreateAsync(TestContext.Current.CancellationToken));
        Assert.Equal("PONGSOLE_TURN_UNAVAILABLE", error.Message);
    }

    [Fact]
    public async Task TimesOutWithoutLeakingCloudflareDetailsAndHonorsCallerCancellation()
    {
        var time = new FakeTimeProvider();
        var token = TestContext.Current.CancellationToken;
        using var client = new HttpClient(new Handler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }));
        var provider = Create(client, time);
        var pending = provider.CreateAsync(token);
        time.Advance(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<TurnCredentialException>(() => pending);
        Assert.Equal("PONGSOLE_TURN_UNAVAILABLE", error.Message);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var cancelled = provider.CreateAsync(cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    private static CloudflareTurnCredentialProvider Create(HttpClient client, TimeProvider time) =>
        new(client, new TurnOptions { KeyId = "test/key", ApiToken = "fixture-token" }, time);

    private static HttpResponseMessage Respond(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
