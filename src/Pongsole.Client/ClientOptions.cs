namespace Pongsole.Client;

internal sealed record ClientOptions(Uri? Endpoint, string? Name)
{
    public static ClientOptions Parse(string[] args)
    {
        Uri? endpoint = null;
        string? name = null;

        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Usage: Pongsole.Client [--server URL] [--name DISPLAYNAME]");
            }

            var value = args[index + 1];
            switch (args[index])
            {
                case "--server" when endpoint is null:
                    endpoint = ParseEndpoint(value);
                    break;
                case "--name" when name is null:
                    name = ParseName(value);
                    break;
                default:
                    throw new ArgumentException("Usage: Pongsole.Client [--server URL] [--name DISPLAYNAME]");
            }
        }

        return new ClientOptions(endpoint, name);
    }

    public static Uri ParseEndpoint(string value)
    {
        var input = value.Trim();
        if (!Uri.TryCreate(input, UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https")
            || input.IndexOfAny(['?', '#', '@', '\\']) >= 0
            || origin.AbsolutePath != "/"
            || !HasOriginPath(input)
            || (origin.Scheme == "http" && !HasLoopbackAuthority(input)))
        {
            throw new ArgumentException("Enter an HTTPS origin or a loopback HTTP origin, without a path, credentials, query or fragment.");
        }

        return new Uri(origin, "/game");
    }

    public static bool IsLoopback(Uri endpoint) =>
        endpoint.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (System.Net.IPAddress.TryParse(endpoint.IdnHost, out var address) && System.Net.IPAddress.IsLoopback(address));

    private static bool HasLoopbackAuthority(string input)
    {
        var authority = input[(input.IndexOf("://", StringComparison.Ordinal) + 3)..].TrimEnd('/');
        var host = authority.StartsWith('[') ? authority[..(authority.IndexOf(']') + 1)] : authority.Split(':', 2)[0];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address));
    }

    private static bool HasOriginPath(string input)
    {
        var separator = input.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        var path = input.IndexOf('/', separator + 3);
        return path < 0 || path == input.Length - 1;
    }

    public static string ParseName(string value)
    {
        var name = value.Trim();
        if (name.Length is < 1 or > 24 || name.Any(char.IsControl))
        {
            throw new ArgumentException("Enter a display name of 1–24 characters without control characters.");
        }

        return name;
    }
}
