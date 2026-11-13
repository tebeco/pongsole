using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pongsole.Server;

internal sealed class TurnOptions
{
    public string? KeyId { get; set; }
    public string? ApiToken { get; set; }
}

internal sealed record TurnIceServer(ImmutableArray<string> Urls, string Username, string Credential)
{
    public override string ToString() => nameof(TurnIceServer);
}

internal sealed record TurnCredentials(ImmutableArray<TurnIceServer> IceServers, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(TurnCredentials);
}

internal sealed class TurnCredentialException(string marker) : Exception(marker);

internal interface ITurnCredentialProvider
{
    Task<TurnCredentials> CreateAsync(CancellationToken cancellationToken);
}

internal sealed class CloudflareTurnCredentialProvider(HttpClient client, TurnOptions options, TimeProvider time)
    : ITurnCredentialProvider
{
    internal const string ApiHost = "rtc.live.cloudflare.com";
    private const int TtlSeconds = 3600;
    private const int MaximumBodyBytes = 64 * 1024;

    public async Task<TurnCredentials> CreateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(options.KeyId) || string.IsNullOrWhiteSpace(options.ApiToken))
            throw new TurnCredentialException("PONGSOLE_TURN_NOT_CONFIGURED");
        if (options.KeyId is "." or "..")
            throw Unavailable();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        var token = deadline.Token;
        var expiresAt = time.GetUtcNow().AddSeconds(TtlSeconds);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"https://{ApiHost}/v1/turn/keys/{Uri.EscapeDataString(options.KeyId)}/credentials/generate-ice-servers");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
            request.Content = JsonContent.Create(new { ttl = TtlSeconds });
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode != HttpStatusCode.Created ||
                response.Content.Headers.ContentLength > MaximumBodyBytes)
                throw Unavailable();

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[MaximumBodyBytes + 1];
            var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, token);
            if (length > MaximumBodyBytes)
                throw Unavailable();

            using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
            return new TurnCredentials(ReadServers(document.RootElement), expiresAt);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is HttpRequestException or IOException or OperationCanceledException or JsonException or
                InvalidOperationException or FormatException or ArgumentException)
        {
            throw Unavailable();
        }
    }

    private static ImmutableArray<TurnIceServer> ReadServers(JsonElement root)
    {
        if (!root.TryGetProperty("iceServers", out var servers) || servers.ValueKind != JsonValueKind.Array)
            throw Unavailable();

        var result = ImmutableArray.CreateBuilder<TurnIceServer>();
        foreach (var server in servers.EnumerateArray())
        {
            if (!server.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
                continue;

            var accepted = urls.EnumerateArray().Where(url => url.ValueKind == JsonValueKind.String)
                .Select(url => url.GetString()!)
                .Where(url => url is "turn:turn.cloudflare.com:3478?transport=udp" or
                    "turn:turn.cloudflare.com:443?transport=udp")
                .Distinct(StringComparer.Ordinal).ToImmutableArray();
            if (accepted.IsEmpty)
                continue;

            var username = server.TryGetProperty("username", out var user) ? user.GetString() : null;
            var credential = server.TryGetProperty("credential", out var secret) ? secret.GetString() : null;
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(credential))
                throw Unavailable();

            result.Add(new TurnIceServer(accepted, username, credential));
        }

        return result.Count == 0 ? throw Unavailable() : result.ToImmutable();
    }

    private static TurnCredentialException Unavailable() => new("PONGSOLE_TURN_UNAVAILABLE");
}
