namespace Pongsole.Contracts;

public sealed record JoinResult(
    Guid PlayerId,
    Team Team,
    string ResumeToken,
    long LastProcessedSequence,
    GameSnapshot Snapshot);
