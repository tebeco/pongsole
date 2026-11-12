namespace Pongsole.Contracts;

public sealed record ArenaState(
    long Tick,
    long LeftScore,
    long RightScore,
    Phase Phase,
    int ServeTicksRemaining,
    PaddleState LeftPaddle,
    PaddleState RightPaddle,
    BallState Ball);
