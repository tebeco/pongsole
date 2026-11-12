using Pongsole.Contracts;

namespace Pongsole.Core;

public sealed class GameEngine
{
    private const double TickDuration = 1.0 / 60;
    private const double ArenaWidth = 100;
    private const double ArenaHeight = 30;
    private const double PaddleHalfHeight = 3;
    private const double BallRadius = 0.5;
    private const double PaddleHalfWidth = 0.5;
    private const double LeftPaddleFace = 2 + PaddleHalfWidth + BallRadius;
    private const double RightPaddleFace = ArenaWidth - LeftPaddleFace;
    private const double MaxBallSpeed = 45;
    private const double MaxReboundAngle = Math.PI / 3;
    private const double CoincidentContactTolerance = 1e-12;
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

        if (next.Phase == Phase.Playing)
        {
            return State = AdvanceBall(next);
        }

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

        return State = next;
    }

    private ArenaState AdvanceBall(ArenaState arena)
    {
        const double top = BallRadius;
        const double bottom = ArenaHeight - BallRadius;
        var (x, y, velocityX, velocityY) = arena.Ball;
        var remaining = TickDuration;

        while (remaining > 0)
        {
            var wallY = velocityY < 0 ? top : bottom;
            var wallTime = TimeToPlane(y, velocityY, wallY);

            var movingLeft = velocityX < 0;
            var paddleX = movingLeft ? LeftPaddleFace : RightPaddleFace;
            var paddleY = movingLeft ? arena.LeftPaddle.Y : arena.RightPaddle.Y;
            var paddleTime = TimeToPlane(x, velocityX, paddleX);
            var impactY = paddleTime <= remaining ? y + velocityY * paddleTime : y;
            if (paddleTime <= remaining &&
                paddleTime <= wallTime + CoincidentContactTolerance &&
                Math.Abs(impactY - paddleY) <= PaddleHalfHeight + BallRadius)
            {
                var wallContact = Math.Abs(paddleTime - wallTime) <= CoincidentContactTolerance;
                x = paddleX;
                y = wallContact ? wallY : impactY;
                var offset = Math.Clamp((y - paddleY) / PaddleHalfHeight, -1, 1);
                var angle = offset * MaxReboundAngle;
                var speed = Math.Min(double.Hypot(velocityX, velocityY) * 1.05, MaxBallSpeed);
                velocityX = (movingLeft ? 1 : -1) * speed * Math.Cos(angle);
                velocityY = speed * Math.Sin(angle);
                if (wallContact)
                {
                    velocityY = wallY == top ? Math.Abs(velocityY) : -Math.Abs(velocityY);
                }

                remaining -= paddleTime;
                continue;
            }

            var goalX = movingLeft ? -BallRadius : ArenaWidth + BallRadius;
            var outside = movingLeft ? x <= goalX : x >= goalX;
            var goalTime = outside ? 0 : TimeToPlane(x, velocityX, goalX);
            if (goalTime <= remaining && goalTime <= wallTime)
            {
                _serveDirection = movingLeft ? -1 : 1;
                return arena with
                {
                    LeftScore = arena.LeftScore + (movingLeft ? 0 : 1),
                    RightScore = arena.RightScore + (movingLeft ? 1 : 0),
                    Phase = Phase.Serving,
                    ServeTicksRemaining = ServeCountdown,
                    Ball = CenteredBall,
                };
            }

            if (wallTime <= remaining)
            {
                x += velocityX * wallTime;
                y = wallY;
                velocityY = -velocityY;
                remaining -= wallTime;
                continue;
            }

            x += velocityX * remaining;
            y += velocityY * remaining;
            break;
        }

        return arena with { Ball = new BallState(x, y, velocityX, velocityY) };
    }

    private static double TimeToPlane(double position, double velocity, double plane)
    {
        if (velocity == 0)
        {
            return double.PositiveInfinity;
        }

        var time = (plane - position) / velocity;
        return time >= 0 ? time : double.PositiveInfinity;
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
