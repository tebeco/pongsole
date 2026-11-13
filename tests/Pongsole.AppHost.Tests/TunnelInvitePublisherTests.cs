using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;

namespace Pongsole.AppHost.Tests;

public sealed class TunnelInvitePublisherTests
{
    [Fact]
    public void InviteContainsEscapedCodeAndValidatedPublicRoot()
    {
        var address = Assert.IsType<Uri>(TunnelInvitePublisher.ParsePublicAddress("https://one.trycloudflare.com:443"));
        const string code = " +/&=#☃ ";
        var href = TunnelInvitePublisher.BuildInvite(address, code);
        var invite = new Uri(href);
        Assert.Equal("one.trycloudflare.com", invite.Host);
        Assert.Equal("/", invite.AbsolutePath);
        Assert.Equal("", invite.Query);
        Assert.StartsWith("#code=", invite.Fragment);
        Assert.Equal(code, Uri.UnescapeDataString(href[(href.IndexOf('#') + 6)..]));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("http://one.trycloudflare.com/")]
    [InlineData("https://trycloudflare.com/")]
    [InlineData("https://one.two.trycloudflare.com/")]
    [InlineData("https://one.trycloudflare.com.evil.test/")]
    [InlineData("https://me@one.trycloudflare.com/")]
    [InlineData("https://one.trycloudflare.com:444/")]
    [InlineData("https://one.trycloudflare.com/game")]
    [InlineData("https://one.trycloudflare.com/?")]
    [InlineData("https://one.trycloudflare.com/#")]
    public void RejectsNonTunnelRoots(string address) => Assert.Null(TunnelInvitePublisher.ParsePublicAddress(address));

    [Theory]
    [InlineData("http://localhost:1234/", true)]
    [InlineData("http://127.0.0.1:1234/", true)]
    [InlineData("http://[::1]:1234/", true)]
    [InlineData("http://example.test:1234/", false)]
    [InlineData("http://localhost:1234/other", false)]
    [InlineData("http://user@localhost:1234/", false)]
    [InlineData("https://localhost:1234/", false)]
    public void OnlyPollsLoopbackHttpRoots(string endpoint, bool expected) =>
        Assert.Equal(expected, TunnelInvitePublisher.IsLocalEndpoint(endpoint));

    [Fact]
    public void UpdatesAndRemovesOnlyTheInviteWhilePreservingOtherState()
    {
        var start = new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
        var local = new UrlSnapshot("http", "http://localhost:1234/", true);
        var snapshot = new CustomResourceSnapshot
        {
            ResourceType = "Project",
            Properties = [],
            StartTimeStamp = start,
            State = new ResourceStateSnapshot(KnownResourceStates.Running, null),
            Urls = ImmutableArray.Create(local),
        };
        var first = TunnelInvitePublisher.UpdateInvite(snapshot, start, "https://one.trycloudflare.com/#code=fixture");
        Assert.Equal(2, first.Urls.Length);
        Assert.Same(local, first.Urls[0]);
        Assert.Equal("Play over Internet", first.Urls[1].DisplayProperties?.DisplayName);
        Assert.Same(first, TunnelInvitePublisher.UpdateInvite(first, start, first.Urls[1].Url));

        var stopped = first with { State = new ResourceStateSnapshot(KnownResourceStates.Finished, null) };
        Assert.Equal(local, Assert.Single(TunnelInvitePublisher.UpdateInvite(stopped, start, first.Urls[1].Url).Urls));

        var restarted = first with { StartTimeStamp = start.AddSeconds(1) };
        Assert.Equal(local, Assert.Single(TunnelInvitePublisher.UpdateInvite(restarted, start, first.Urls[1].Url).Urls));
        var replacement = TunnelInvitePublisher.UpdateInvite(restarted, restarted.StartTimeStamp,
            "https://two.trycloudflare.com/#code=fixture");
        Assert.Equal(2, replacement.Urls.Length);
        Assert.Equal(restarted.StartTimeStamp, replacement.StartTimeStamp);
        Assert.Contains("two.trycloudflare.com", replacement.Urls[1].Url);
        Assert.Equal(local, Assert.Single(TunnelInvitePublisher.UpdateInvite(replacement, restarted.StartTimeStamp, null).Urls));
    }
}
