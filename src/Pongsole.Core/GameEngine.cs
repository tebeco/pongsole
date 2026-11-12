using Pongsole.Contracts;

namespace Pongsole.Core;

public sealed class GameEngine
{
    private const double TickDuration = 1.0 / 60;
    private const double ArenaWidth = 100;
    private const double ArenaHeight = 30;
    private const double PaddleHalfHeight = 3;
    private const double MaxPaddleSpeed = 20;
    private const int ServeCountdown = 60;
    private const double ServeSpeed = 24;
    private const double MaxServeAngle = Math.PI / 6;
    private static readonly double PaddleDecay = Math.Pow(2, -1.0 / 21);

    private static readonly PaddleState CenteredPaddle = new(ArenaHeight / 2, 0);
    private static readonly BallState CenteredBall = new(ArenaWidth / 2, ArenaHeight / 2, 0, 0);

    private readonly Random _random;
    private int _serveDirection;

    public GameEngine(int randomSeed)
        : this(randomSeed, new ArenaState(
            Tick: 0,
            LeftScore: 0,
            RightScore: 0,
            Phase.Waiting,
            ServeTicksRemaining: 0,
            CenteredPaddle,
            CenteredPaddle,
            CenteredBall))
    {
    }

    internal GameEngine(int randomSeed, ArenaState initialState)
    {
        _random = new Random(randomSeed);
        _serveDirection = RandomServeDirection();
        State = initialState;
    }

    public ArenaState State { get; private set; }

    public ArenaState Advance(int leftPushes, int rightPushes, bool leftPresent, bool rightPresent)
    {
        var next = State with { Tick = State.Tick + 1 };
        if (!leftPresent || !rightPresent)
        {
            if (State.Phase != Phase.Waiting)
            {
                _serveDirection = RandomServeDirection();
            }

            return State = next with
            {
                Phase = Phase.Waiting,
                ServeTicksRemaining = 0,
                LeftPaddle = CenteredPaddle,
                RightPaddle = CenteredPaddle,
                Ball = CenteredBall,
            };
        }

        if (next.Phase == Phase.Waiting)
        {
            return State = next with
            {
                Phase = Phase.Serving,
                ServeTicksRemaining = ServeCountdown,
            };
        }

        next = next with
        {
            LeftPaddle = AdvancePaddle(next.LeftPaddle, leftPushes),
            RightPaddle = AdvancePaddle(next.RightPaddle, rightPushes),
        };

        if (next.Phase == Phase.Serving)
        {
            next = next with { ServeTicksRemaining = next.ServeTicksRemaining - 1 };
            if (next.ServeTicksRemaining == 0)
            {
                var angle = (_random.NextDouble() * 2 - 1) * MaxServeAngle;
                next = next with
                {
                    Phase = Phase.Playing,
                    Ball = CenteredBall with
                    {
                        VelocityX = _serveDirection * ServeSpeed * Math.Cos(angle),
                        VelocityY = ServeSpeed * Math.Sin(angle),
                    },
                };
            }
        }

        return State = next;
    }

    private int RandomServeDirection() => _random.Next(2) == 0 ? -1 : 1;

    private static PaddleState AdvancePaddle(PaddleState paddle, int pushes)
    {
        const double minY = PaddleHalfHeight;
        const double maxY = ArenaHeight - PaddleHalfHeight;
        var velocity = Math.Clamp(paddle.Velocity + 2.0 * pushes, -MaxPaddleSpeed, MaxPaddleSpeed);
        var y = Math.Clamp(paddle.Y + velocity * TickDuration, minY, maxY);
        if ((y == minY && velocity < 0) || (y == maxY && velocity > 0))
        {
            velocity = 0;
        }

        return new PaddleState(y, velocity * PaddleDecay);
    }
}
