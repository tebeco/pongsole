using System.Net;
using System.Text.RegularExpressions;

namespace Pongsole.Tunnel;

internal static partial class TunnelAddress
{
    internal static Uri ParseOrigin(string? input)
    {
        if (input is null || !OriginPattern().IsMatch(input) ||
            !Uri.TryCreate(input, UriKind.Absolute, out var origin) ||
            !(origin.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                IPAddress.TryParse(origin.Host.Trim('[', ']'), out var address) && IsLoopback(address)) ||
            origin.Port <= 0)
        {
            throw new InvalidOperationException("Tunnel:Origin must be an HTTP loopback origin.");
        }

        return origin;
    }

    internal static Uri? ReadPublicAddress(string line)
    {
        // The CLI prints the URL by itself in a pipe-delimited banner.
        var candidate = line.Trim();
        var first = candidate.IndexOf('|');
        if (first >= 0 && candidate.EndsWith('|'))
            candidate = candidate[(first + 1)..^1].Trim();

        return PublicPattern().IsMatch(candidate) && Uri.TryCreate(candidate, UriKind.Absolute, out var address)
            ? address
            : null;
    }

    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    [GeneratedRegex(@"\Ahttp://(?:[a-z0-9.]+|\[[0-9a-f:.]+\])(?::[0-9]{1,5})?/?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OriginPattern();

    [GeneratedRegex(@"\Ahttps://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com(?::443)?/?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublicPattern();
}
