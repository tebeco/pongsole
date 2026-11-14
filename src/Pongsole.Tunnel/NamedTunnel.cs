namespace Pongsole.Tunnel;

internal sealed class NamedTunnel(Uri? address, string? token)
{
    internal Uri? Address { get; } = address;
    internal string? Token { get; } = token;

    internal static NamedTunnel FromConfiguration(IConfiguration configuration)
    {
        var hostname = configuration["Tunnel:PublicHostname"];
        var token = configuration["Tunnel:Token"];
        if (string.IsNullOrWhiteSpace(hostname) && string.IsNullOrWhiteSpace(token))
            return new NamedTunnel(null, null);

        if (string.IsNullOrWhiteSpace(token) || Uri.CheckHostName(hostname) != UriHostNameType.Dns ||
            !hostname!.Contains('.') || hostname.EndsWith(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A named tunnel requires Tunnel:PublicHostname and Tunnel:Token.");
        }

        return new NamedTunnel(new UriBuilder(Uri.UriSchemeHttps, hostname).Uri, token);
    }
}
