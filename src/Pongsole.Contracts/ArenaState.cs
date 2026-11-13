using System.Collections.Immutable;

namespace Pongsole.Contracts;

public sealed record ArenaState(
    long Tick,
    long LeftScore,
    long RightScore,
    Phase Phase,
    int ServeTicksRemaining,
    PaddleState LeftPaddle,
    PaddleState RightPaddle,
    ImmutableArray<BallState> Balls,
    ImmutableArray<PickupState> Pickups);
