using System.Collections.Immutable;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace Pongsole.AppHost;

internal sealed partial class TunnelInvitePublisher(
    ResourceNotificationService notifications,
    IResource resource,
    EndpointReference endpoint,
    ParameterResource gameCode) : BackgroundService
{
    private const string InviteName = "public-game-invite";
    private readonly HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ReconcileAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        if (!notifications.TryGetCurrentState(resource.Name, out var current))
            return;

        string? invite = null;
        if (current.Snapshot.State?.Text == KnownResourceStates.Running &&
            endpoint.IsAllocated && IsLocalEndpoint(endpoint.Url))
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                var token = deadline.Token;
                using var response = await client.GetAsync(endpoint.Url, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode == HttpStatusCode.Found &&
                    ParsePublicAddress(response.Headers.Location?.OriginalString) is { } address)
                {
                    var code = await gameCode.GetValueAsync(token);
                    if (!string.IsNullOrWhiteSpace(code))
                        invite = BuildInvite(address, code);
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                error is HttpRequestException or OperationCanceledException or InvalidOperationException or FormatException)
            {
                // An unavailable tunnel or unresolved parameter means there is no invite to show.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        await notifications.PublishUpdateAsync(resource, current.ResourceId,
            snapshot => UpdateInvite(snapshot, current.Snapshot.StartTimeStamp, invite));
    }

    internal static CustomResourceSnapshot UpdateInvite(CustomResourceSnapshot snapshot, DateTime? observedStart,
        string? invite)
    {
        if (snapshot.StartTimeStamp != observedStart || snapshot.State?.Text != KnownResourceStates.Running)
            invite = null;

        var existing = snapshot.Urls.Where(url => url.Name == InviteName).ToArray();
        if (invite is null ? existing.Length == 0 : existing.Length == 1 && existing[0].Url == invite)
            return snapshot;

        var urls = snapshot.Urls.Where(url => url.Name != InviteName).ToImmutableArray();
        if (invite is not null)
            urls = urls.Add(new UrlSnapshot(InviteName, invite, IsInternal: false)
            {
                DisplayProperties = new UrlDisplayPropertiesSnapshot("Play over Internet", 100)
            });

        return snapshot with { Urls = urls };
    }

    internal static bool IsLocalEndpoint(string input) =>
        Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp &&
        uri.IsLoopback && uri.UserInfo.Length == 0 && uri.PathAndQuery == "/" && uri.Fragment.Length == 0;

    internal static Uri? ParsePublicAddress(string? input) =>
        input is not null && PublicAddress().IsMatch(input) && Uri.TryCreate(input, UriKind.Absolute, out var uri)
            ? uri : null;

    internal static string BuildInvite(Uri publicAddress, string code) =>
        publicAddress.AbsoluteUri + "#code=" + Uri.EscapeDataString(code);

    public override void Dispose()
    {
        base.Dispose();
        client.Dispose();
    }

    [GeneratedRegex(@"\Ahttps://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.trycloudflare\.com(?::443)?/?\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PublicAddress();
}
