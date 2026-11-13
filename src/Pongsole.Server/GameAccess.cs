using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;

namespace Pongsole.Server;

internal sealed class GameAccess
{
    private readonly byte[]? codeHash;
    private readonly ForwardedHeadersOptions? forwarding;

    internal GameAccess(IConfiguration configuration)
    {
        var mode = configuration["Pong:Mode"] ?? "Loopback";
        if (mode.Equals("Private", StringComparison.OrdinalIgnoreCase))
        {
            var code = configuration["PONGSOLE_SHARED_CODE"];
            if (string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Private Pongsole access requires PONGSOLE_SHARED_CODE.");
            codeHash = HashCode(code);
        }
        else if (!mode.Equals("Loopback", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Pong:Mode must be Loopback or Private.");
        }

        var proxies = configuration.GetSection("Pong:KnownProxies").GetChildren().ToArray();
        if (proxies.Length == 0)
            return;

        forwarding = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };
        forwarding.KnownIPNetworks.Clear();
        forwarding.KnownProxies.Clear();
        foreach (var proxy in proxies)
        {
            if (!IPAddress.TryParse(proxy.Value, out var address))
                throw new InvalidOperationException("Pong:KnownProxies entries must be IP addresses.");
            forwarding.KnownProxies.Add(address);
        }
    }

    internal void Apply(WebApplication app)
    {
        ApplyForwarding(app);

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/rtc"))
            {
                await next(context);
                return;
            }

            var status = await RejectionStatusAsync(context);
            if (status is int rejection)
                context.Response.StatusCode = rejection;
            else
                await next(context);
        });
    }

    internal void ApplyForwarding(IApplicationBuilder app)
    {
        if (codeHash is not null && forwarding is not null)
            app.UseForwardedHeaders(forwarding);
    }

    internal bool IsLoopback => codeHash is null;

    internal async Task<int?> RejectionStatusAsync(HttpContext context)
    {
        var request = context.Request;
        var hasOrigin = request.Headers.ContainsKey("Origin");
        if (hasOrigin && !BrowserAccess.HasSameOrigin(request))
            return StatusCodes.Status403Forbidden;
        var isSignaling = request.Path.StartsWithSegments("/rtc");
        if (isSignaling)
            context.Response.Headers.CacheControl = "no-store";
        if (hasOrigin && isSignaling &&
            (!request.Headers.TryGetValue("X-Pong-Browser", out var marker) || marker.Count != 1 || marker[0] != "1"))
            return StatusCodes.Status403Forbidden;

        if (IsLoopback)
        {
            var local = hasOrigin ? IsLocalBrowser(context) : IsLocalPeer(context);
            return local ? null : StatusCodes.Status403Forbidden;
        }

        if (!request.IsHttps || !hasOrigin || !isSignaling && request.Headers.ContainsKey("X-Pong-Code"))
            return CodeRejection(request);

        return hasOrigin && await BrowserAccess.HasTicketAsync(context)
            ? null
            : StatusCodes.Status401Unauthorized;
    }

    internal int? CodeRejection(HttpRequest request)
    {
        if (!request.IsHttps)
            return StatusCodes.Status403Forbidden;
        if (!request.Headers.TryGetValue("X-Pong-Code", out var supplied))
            return StatusCodes.Status401Unauthorized;
        if (supplied.Count != 1 || !CryptographicOperations.FixedTimeEquals(codeHash!, HashCode(supplied.ToString())))
            return StatusCodes.Status403Forbidden;
        return null;
    }

    internal static bool IsLocalBrowser(HttpContext context)
    {
        var host = context.Request.Host.Host;
        return IsLocalPeer(context) && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            IPAddress.TryParse(host.Trim('[', ']'), out var address) && IsLoopbackAddress(address));
    }

    private static bool IsLocalPeer(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } peer && IsLoopbackAddress(peer);

    private static bool IsLoopbackAddress(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static byte[] HashCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));
}
