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
        if (codeHash is not null && forwarding is not null)
            app.UseForwardedHeaders(forwarding);

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/game"))
            {
                await next(context);
                return;
            }

            var status = RejectionStatus(context);
            if (status is int rejection)
                context.Response.StatusCode = rejection;
            else
                await next(context);
        });
    }

    private int? RejectionStatus(HttpContext context)
    {
        if (codeHash is null)
        {
            var peer = context.Connection.RemoteIpAddress;
            if (peer?.IsIPv4MappedToIPv6 == true)
                peer = peer.MapToIPv4();
            return peer is not null && IPAddress.IsLoopback(peer) ? null : StatusCodes.Status403Forbidden;
        }

        if (!context.Request.IsHttps)
            return StatusCodes.Status403Forbidden;
        if (!context.Request.Headers.TryGetValue("X-Pong-Code", out var supplied))
            return StatusCodes.Status401Unauthorized;
        if (supplied.Count != 1 || !CryptographicOperations.FixedTimeEquals(codeHash, HashCode(supplied.ToString())))
            return StatusCodes.Status403Forbidden;
        return null;
    }

    private static byte[] HashCode(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));
}
