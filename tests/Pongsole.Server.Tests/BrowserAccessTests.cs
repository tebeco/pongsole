using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Net.Http.Headers;
using Pongsole.Contracts;

namespace Pongsole.Server.Tests;

public sealed class BrowserAccessTests
{
    private const string PrivateOrigin = "https://pong.example";
    private const string Code = "test-browser-code";
    private const string CookieName = "__Host-PongsoleAccess";

    [Fact]
    public async Task PrivateExchangeCreatesOnlySessionCookieAndReportsAuthorization()
    {
        await using var app = new BrowserServer();
        using var client = app.Client(PrivateOrigin);
        await AssertSession(client, "private", false);
        var ticket = await SignIn(client, PrivateOrigin);
        Assert.Equal(CookieName, ticket.Name.ToString());
        Assert.Equal("/", ticket.Path.ToString());
        Assert.False(ticket.Domain.HasValue);
        Assert.Null(ticket.Expires);
        Assert.Null(ticket.MaxAge);
        Assert.True(ticket.HttpOnly);
        Assert.True(ticket.Secure);
        Assert.Equal(SameSiteMode.Strict, ticket.SameSite);
        await AssertSession(client, "private", true, Cookie(ticket), PrivateOrigin);
    }

    [Theory]
    [InlineData(null, "1", Code, HttpStatusCode.Forbidden)]
    [InlineData("null", "1", Code, HttpStatusCode.Forbidden)]
    [InlineData("https://other.example", "1", Code, HttpStatusCode.Forbidden)]
    [InlineData("https://pong.example:444", "1", Code, HttpStatusCode.Forbidden)]
    [InlineData("http://pong.example", "1", Code, HttpStatusCode.Forbidden)]
    [InlineData("https://pong.example/", "1", Code, HttpStatusCode.Forbidden)]
    [InlineData("https://pong.example https://other.example", "1", Code, HttpStatusCode.Forbidden)]
    [InlineData(PrivateOrigin, null, Code, HttpStatusCode.Forbidden)]
    [InlineData(PrivateOrigin, "0", Code, HttpStatusCode.Forbidden)]
    [InlineData(PrivateOrigin, "1", null, HttpStatusCode.Unauthorized)]
    [InlineData(PrivateOrigin, "1", "wrong", HttpStatusCode.Forbidden)]
    public async Task ExchangeRejectsBadOriginMarkerOrCode(string? origin, string? marker, string? code, HttpStatusCode status)
    {
        await using var app = new BrowserServer();
        using var client = app.Client(PrivateOrigin);
        using var request = Exchange(origin, marker, code);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        AssertStatus(response, status);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task PrivateRejectsInsecureEndpointsAndQueryCredentials()
    {
        await using var app = new BrowserServer();
        using var insecure = app.Client("http://pong.example");
        using var post = Exchange("http://pong.example", "1", Code);
        using var exchange = await insecure.SendAsync(post, TestContext.Current.CancellationToken);
        AssertStatus(exchange, HttpStatusCode.Forbidden);
        Assert.False(exchange.Headers.Contains("Set-Cookie"));
        using var status = await insecure.GetAsync("/browser/session", TestContext.Current.CancellationToken);
        AssertStatus(status, HttpStatusCode.Forbidden);
        using var secure = app.Client(PrivateOrigin);
        using var query = new HttpRequestMessage(HttpMethod.Post, "/browser/session?X-Pong-Code=ignored");
        Add(query, "Origin", PrivateOrigin);
        Add(query, "X-Pong-Browser", "1");
        using var response = await secure.SendAsync(query, TestContext.Current.CancellationToken);
        AssertStatus(response, HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("https://other.example")]
    [InlineData("https://pong.example:444")]
    public async Task SessionGetRejectsSuppliedInvalidOrigin(string origin)
    {
        await using var app = new BrowserServer();
        using var client = app.Client(PrivateOrigin);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/browser/session");
        Add(request, "Origin", origin);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        AssertStatus(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TicketRejectsTamperingAndExpiresAtEightHoursWithoutSliding()
    {
        await using var app = new BrowserServer();
        using var client = app.Client(PrivateOrigin);
        var ticket = await SignIn(client, PrivateOrigin);
        var value = ticket.Value.ToString();
        var index = value.Length / 2;
        var changed = string.Concat(value.AsSpan(0, index), value[index] == 'A' ? "B" : "A", value.AsSpan(index + 1));
        await AssertSession(client, "private", false, $"{CookieName}={changed}");

        app.Time.Advance(TimeSpan.FromHours(7));
        await AssertSession(client, "private", true, Cookie(ticket));
        app.Time.Advance(TimeSpan.FromHours(1));
        await AssertSession(client, "private", false, Cookie(ticket));
    }

    [Fact]
    public async Task RestartRejectsOldTicketEvenWithSameEncryptionKeys()
    {
        var keys = new EphemeralDataProtectionProvider();
        string cookie;
        await using (var first = new BrowserServer(keys: keys))
        {
            using var client = first.Client(PrivateOrigin);
            cookie = Cookie(await SignIn(client, PrivateOrigin));
            await AssertSession(client, "private", true, cookie);
        }

        await using var restarted = new BrowserServer(keys: keys);
        using var afterRestart = restarted.Client(PrivateOrigin);
        await AssertSession(afterRestart, "private", false, cookie);
    }

    [Theory]
    [InlineData("127.0.0.1", "http://localhost", HttpStatusCode.OK)]
    [InlineData("::1", "http://[::1]", HttpStatusCode.OK)]
    [InlineData("::ffff:127.0.0.1", "http://127.0.0.1:8080", HttpStatusCode.OK)]
    [InlineData("127.0.0.1", "http://public.example", HttpStatusCode.Forbidden)]
    [InlineData("192.0.2.4", "http://localhost", HttpStatusCode.Forbidden)]
    public async Task LoopbackRequiresBothBrowserHostAndPeer(string peer, string origin, HttpStatusCode status)
    {
        await using var app = new BrowserServer(mode: "Loopback", peer: peer);
        using var client = app.Client(origin);
        if (status == HttpStatusCode.OK)
            await AssertSession(client, "loopback", true);
        else
        {
            using var denied = await client.GetAsync("/browser/session", TestContext.Current.CancellationToken);
            AssertStatus(denied, status);
        }

        using var post = Exchange(origin, "1", null);
        using var response = await client.SendAsync(post, TestContext.Current.CancellationToken);
        AssertStatus(response, status == HttpStatusCode.OK ? HttpStatusCode.NoContent : status);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(status, await Negotiate(client, origin));
    }

    [Fact]
    public async Task GameGatePreservesConsoleAccessAndRequiresOriginForCookie()
    {
        await using var app = new BrowserServer();
        using var client = app.Client(PrivateOrigin);
        var cookie = Cookie(await SignIn(client, PrivateOrigin));
        Assert.Equal(HttpStatusCode.OK, await Negotiate(client, null, Code));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate(client, null, "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Negotiate(client, null, cookie: cookie));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate(client, "null", Code, cookie));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate(client, "https://other.example", Code, cookie));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate(client, PrivateOrigin, "wrong", cookie));
        Assert.Equal(HttpStatusCode.OK, await Negotiate(client, PrivateOrigin, cookie: cookie));
    }

    [Fact]
    public async Task LoopbackRejectsForeignBrowserOriginButAllowsOriginlessConsole()
    {
        await using var app = new BrowserServer(mode: "Loopback");
        using var client = app.Client("http://localhost");
        Assert.Equal(HttpStatusCode.OK, await Negotiate(client, null));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate(client, "null"));
        Assert.Equal(HttpStatusCode.Forbidden, await Negotiate(client, "http://other.example"));
        using var rebound = app.Client("http://rebound.example");
        Assert.Equal(HttpStatusCode.OK, await Negotiate(rebound, null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OnlyConfiguredProxyCanSupplyExternalHttps(bool trusted)
    {
        await using var app = new BrowserServer(peer: trusted ? "192.0.2.4" : "192.0.2.5", proxy: "192.0.2.4");
        using var client = app.Client("http://pong.example");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "other.example");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.6");

        if (trusted)
        {
            var cookie = Cookie(await SignIn(client, PrivateOrigin));
            await AssertSession(client, "private", true, cookie, PrivateOrigin);
            Assert.Equal(HttpStatusCode.OK, await Negotiate(client, PrivateOrigin, cookie: cookie));
            Assert.Equal(HttpStatusCode.Forbidden, await Negotiate(client, "https://other.example", cookie: cookie));
        }
        else
        {
            using var request = Exchange(PrivateOrigin, "1", Code);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            AssertStatus(response, HttpStatusCode.Forbidden);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Theory]
    [InlineData(true, "https")]
    [InlineData(false, "http")]
    public async Task HomepageMetadataUsesRequestHostAndTrustedForwardedScheme(bool trusted, string scheme)
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = new BrowserServer(peer: trusted ? "192.0.2.4" : "192.0.2.5", proxy: "192.0.2.4");
        using var client = app.Client("http://pong.example:8181");
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "other.example");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.6");

        using var root = await client.GetAsync("/?code=not-in-metadata", token);
        using var index = await client.GetAsync("/index.html", token);
        AssertStatus(root, HttpStatusCode.OK);
        AssertStatus(index, HttpStatusCode.OK);
        Assert.Equal("text/html; charset=utf-8", root.Content.Headers.ContentType?.ToString());
        Assert.True(root.Headers.CacheControl?.NoStore);
        var html = await root.Content.ReadAsStringAsync(token);
        Assert.Equal(html, await index.Content.ReadAsStringAsync(token));
        Assert.Contains("<meta property=\"og:title\" content=\"Pongsole\">", html);
        Assert.Contains($"<meta property=\"og:url\" content=\"{scheme}://pong.example:8181/\">", html);
        Assert.Contains($"<meta property=\"og:image\" content=\"{scheme}://pong.example:8181/images/pongsole-preview.gif\">", html);
        Assert.Contains("<meta property=\"og:image:type\" content=\"image/gif\">", html);
        Assert.Contains("<meta property=\"og:image:width\" content=\"1864\">", html);
        Assert.Contains("<meta property=\"og:image:height\" content=\"726\">", html);
        Assert.Contains("<meta name=\"twitter:card\" content=\"summary_large_image\">", html);
        Assert.Contains("src=\"/pongsole.mjs\"", html);
        Assert.DoesNotContain("not-in-metadata", html);
        Assert.DoesNotContain("other.example", html);
        Assert.DoesNotContain(Code, html);

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/index.html");
        using var head = await client.SendAsync(headRequest, token);
        AssertStatus(head, HttpStatusCode.OK);
        Assert.Equal(index.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal(index.Content.Headers.ContentType, head.Content.Headers.ContentType);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(token));
    }

    [Fact]
    public async Task SuppliedGifIsPublicAndByteExactInPrivateMode()
    {
        var token = TestContext.Current.CancellationToken;
        await using var app = new BrowserServer();
        using var client = app.Client(PrivateOrigin);
        using var response = await client.GetAsync("/images/pongsole-preview.gif", token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/gif", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync(token);
        Assert.Equal(10_416_529, bytes.Length);
        Assert.Equal("75D7DC53FD6A8D005ADFED9DCFBC3AF0A705079588357E02A855F73F661E9FFE",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        await AssertSession(client, "private", false);
    }


    [Fact]
    public async Task BrowserCookieAdmitsWebSocketAndJoinsSharedGame()
    {
        await using var app = new BrowserServer(proxy: "127.0.0.1", kestrel: true);
        using var client = app.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false
        });
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        client.BaseAddress = new Uri(Assert.Single(addresses.Addresses));
        var origin = new UriBuilder(client.BaseAddress) { Scheme = "https" }.Uri.GetLeftPart(UriPartial.Authority);
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        var cookie = Cookie(await SignIn(client, origin));

        await using var browser = new HubConnectionBuilder()
            .WithUrl(new Uri(client.BaseAddress!, "/game"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.Headers.Add("Origin", origin);
                options.Headers.Add("Cookie", cookie);
                options.Headers.Add("X-Forwarded-Proto", "https");
            })
            .AddJsonProtocol(options => GameJson.Configure(options.PayloadSerializerOptions))
            .Build();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await browser.StartAsync(timeout.Token);
        var result = await browser.InvokeAsync<JoinResult>("Join", "Browser", null, timeout.Token);
        Assert.Equal("Browser", Assert.Single(result.Snapshot.Players).DisplayName);
    }

    private static HttpRequestMessage Exchange(string? origin, string? marker, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/browser/session");
        Add(request, "Origin", origin);
        Add(request, "X-Pong-Browser", marker);
        Add(request, "X-Pong-Code", code);
        return request;
    }

    private static async Task<SetCookieHeaderValue> SignIn(HttpClient client, string origin)
    {
        using var request = Exchange(origin, "1", Code);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        AssertStatus(response, HttpStatusCode.NoContent);
        return SetCookieHeaderValue.Parse(Assert.Single(response.Headers.GetValues("Set-Cookie")));
    }

    private static async Task AssertSession(HttpClient client, string mode, bool authorized, string? cookie = null,
        string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/browser/session");
        Add(request, "Cookie", cookie);
        Add(request, "Origin", origin);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        AssertStatus(response, HttpStatusCode.OK);
        var status = await response.Content.ReadFromJsonAsync<SessionStatus>();
        Assert.Equal(new SessionStatus(mode, authorized), status);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static async Task<HttpStatusCode> Negotiate(HttpClient client, string? origin, string? code = null, string? cookie = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/game/negotiate?negotiateVersion=1");
        Add(request, "Origin", origin);
        Add(request, "X-Pong-Code", code);
        Add(request, "Cookie", cookie);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private static string Cookie(SetCookieHeaderValue ticket) => $"{ticket.Name}={ticket.Value}";

    private static void AssertStatus(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static void Add(HttpRequestMessage request, string name, string? value)
    {
        if (value is not null)
            Assert.True(request.Headers.TryAddWithoutValidation(name, value));
    }

    private sealed record SessionStatus(string Mode, bool Authorized);

    private sealed class BrowserServer : WebApplicationFactory<Program>
    {
        private readonly string mode;
        private readonly string peer;
        private readonly string? proxy;
        private readonly IDataProtectionProvider keys;
        private readonly bool kestrel;

        internal BrowserServer(string mode = "Private", string peer = "127.0.0.1", string? proxy = null,
            IDataProtectionProvider? keys = null, bool kestrel = false)
        {
            this.mode = mode;
            this.peer = peer;
            this.proxy = proxy;
            this.keys = keys ?? new EphemeralDataProtectionProvider();
            this.kestrel = kestrel;
            if (kestrel)
                UseKestrel(0);
        }

        internal FakeTimeProvider Time { get; } = new();

        internal HttpClient Client(string origin) => CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(origin),
            AllowAutoRedirect = false,
            HandleCookies = false
        });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Pong:Mode"] = mode,
                ["PONGSOLE_SHARED_CODE"] = Code,
                ["Pong:KnownProxies:0"] = proxy
            }.Where(pair => pair.Value is not null)));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                if (!kestrel)
                {
                    services.AddSingleton<TimeProvider>(Time);
                    services.AddSingleton<IStartupFilter>(new SetPeer(IPAddress.Parse(peer)));
                }
                else
                {
                    services.AddSingleton(TimeProvider.System);
                }

                services.RemoveAll<IDataProtectionProvider>();
                services.AddSingleton(keys);
                services.AddLegacyGameTransport();
            });
        }

        private sealed class SetPeer(IPAddress peer) : IStartupFilter
        {
            public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
            {
                app.Use(async (context, continuation) =>
                {
                    context.Connection.RemoteIpAddress = peer;
                    await continuation(context);
                });
                next(app);
            };
        }
    }
}
