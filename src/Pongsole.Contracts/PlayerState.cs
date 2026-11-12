namespace Pongsole.Contracts;

public sealed record PlayerState(Guid PlayerId, string DisplayName, Team Team, long LastProcessedSequence);
