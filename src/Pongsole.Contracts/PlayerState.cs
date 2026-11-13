namespace Pongsole.Contracts;

public sealed record PlayerState(
    Guid PlayerId,
    string DisplayName,
    Team Team,
    long LastProcessedSequence,
    int RecentPushCount = 0,
    long? LastUpTick = null,
    long? LastDownTick = null);
