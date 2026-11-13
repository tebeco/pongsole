using System.Collections.Immutable;

namespace Pongsole.Transport;

public sealed record RtcIceServer(ImmutableArray<string> Urls, string Username, string Credential)
{
    public override string ToString() => nameof(RtcIceServer);
}

public sealed record RtcSession(
    string SessionId, string SessionToken, long Epoch,
    ImmutableArray<RtcIceServer> IceServers, DateTimeOffset ExpiresAt)
{
    public override string ToString() => nameof(RtcSession);
}

public sealed record RtcDescription(string Sdp)
{
    public override string ToString() => nameof(RtcDescription);
}

public sealed record ControlMessage(
    string Type, string? Name = null, string? ResumeToken = null,
    Guid? PlayerId = null, Pongsole.Contracts.Team? Team = null,
    long? LastProcessedSequence = null, string? Code = null,
    Pongsole.Contracts.GameMode? Mode = null)
{
    public const int MaxMessageBytes = 4096;
    public override string ToString() => nameof(ControlMessage);
}
