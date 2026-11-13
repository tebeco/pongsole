namespace Pongsole.Contracts;

public sealed record PlayerPaddleState(Guid PlayerId, Team Team, double Y, double Velocity);
