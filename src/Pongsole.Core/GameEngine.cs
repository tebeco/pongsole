using Pongsole.Contracts;

namespace Pongsole.Core;

public sealed class GameEngine
{
    public GameEngine(int randomSeed)
        : this(randomSeed, new ArenaState(
            Tick: 0,
            LeftScore: 0,
            RightScore: 0,
            Phase.Waiting,
            ServeTicksRemaining: 0,
            new PaddleState(Y: 15, Velocity: 0),
            new PaddleState(Y: 15, Velocity: 0),
            new BallState(X: 50, Y: 15, VelocityX: 0, VelocityY: 0)))
    {
    }

    internal GameEngine(int randomSeed, ArenaState initialState)
    {
        State = initialState;
    }

    public ArenaState State { get; private set; }

    public ArenaState Advance(int leftPushes, int rightPushes, bool leftPresent, bool rightPresent)
    {
        throw new NotImplementedException();
    }
}
