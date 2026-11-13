using System.Collections.Immutable;
using Pongsole.Contracts;

namespace Pongsole.Core;

public sealed class GameEngine
{
    private const double TickDuration = 1.0 / 60;
    private const double ArenaWidth = 100;
    private const double ArenaHeight = 30;
    private const double PaddleHalfHeight = 3;
    private const double IndividualPaddleHalfHeight = 1.5;
    private const double BallRadius = 0.5;
    private const double PaddleHalfWidth = 0.5;
    private const double LeftPaddleFace = 2 + PaddleHalfWidth + BallRadius;
    private const double RightPaddleFace = ArenaWidth - LeftPaddleFace;
    private const double MaxBallSpeed = 45;
    private const double MaxReboundAngle = Math.PI / 3;
    private const double CoincidentContactTolerance = 1e-12;
    private const double PaddleImpulse = 7.2;
    private const double MaxPaddleSpeed = 72;
    private const int ServeCountdown = 60;
    private const double ServeSpeed = 24;
    private const double MaxServeAngle = Math.PI / 6;
    private const int MaxBallCount = 10;
    private const double PickupRadius = 1.6;
    private const double PickupContactRadius = BallRadius + PickupRadius;
    private const int PickupSpawnInterval = 300;
    private const int PickupLifetime = 3600;
    private static readonly double PaddleDecay = Math.Pow(2, -1.0 / 21);

    private static readonly PaddleState CenteredPaddle = new(ArenaHeight / 2, 0);
    private readonly Random _random;
    private int _serveDirection;
    private long _nextBallId;
    private long _nextPickupId;
    private int _pickupSpawnTicks;
    private ImmutableArray<PaddleParticipant> _participants = [];

    public GameEngine(int randomSeed)
        : this(randomSeed, new ArenaState(
            Tick: 0,
            LeftScore: 0,
            RightScore: 0,
            Phase.Waiting,
            ServeTicksRemaining: 0,
            CenteredPaddle,
            CenteredPaddle,
            [CenteredBall(1)],
            []))
    {
    }

    internal GameEngine(int randomSeed, ArenaState initialState)
    {
        _random = new Random(randomSeed);
        _serveDirection = RandomServeDirection();
        var balls = initialState.Balls.Sort(static (first, second) => first.Id.CompareTo(second.Id));
        _nextBallId = balls[^1].Id + 1;
        _nextPickupId = initialState.Pickups.IsEmpty ? 1 : initialState.Pickups.Max(pickup => pickup.Id) + 1;
        State = initialState with { Balls = balls };
    }

    public ArenaState State { get; private set; }

    public void SynchronizeParticipants(GameMode mode, ImmutableArray<PaddleParticipant> participants)
    {
        _participants = participants;
        var modeChanged = State.Mode != mode;
        var bothTeamsPresent = HasBothTeams();
        if (modeChanged || (!bothTeamsPresent && State.Phase != Phase.Waiting))
            State = EnterWaiting(State);

        var previousPaddles = modeChanged ? [] : State.PlayerPaddles;
        State = State with
        {
            Mode = mode,
            PlayerPaddles = mode == GameMode.IndividualPaddles
                ? [.. participants.OrderBy(participant => participant.PlayerId)
                    .Select(participant => previousPaddles.FirstOrDefault(paddle => paddle.PlayerId == participant.PlayerId &&
                        paddle.Team == participant.Team) ?? new PlayerPaddleState(participant.PlayerId, participant.Team,
                        CenteredPaddle.Y, 0))]
                : [],
        };
    }

    public ArenaState Advance(ImmutableArray<PlayerPaddleInput> inputs)
    {
        var pushes = new Dictionary<Guid, int>();
        foreach (var input in inputs)
            pushes[input.PlayerId] = pushes.GetValueOrDefault(input.PlayerId) + input.Pushes;

        var leftPushes = 0;
        var rightPushes = 0;
        if (State.Mode == GameMode.AllTogether)
        {
            foreach (var participant in _participants)
            {
                if (participant.Team == Team.Left)
                    leftPushes += pushes.GetValueOrDefault(participant.PlayerId);
                else
                    rightPushes += pushes.GetValueOrDefault(participant.PlayerId);
            }
        }

        var bothTeamsPresent = HasBothTeams();
        return Advance(leftPushes, rightPushes, bothTeamsPresent, bothTeamsPresent, pushes);
    }

    public ArenaState Advance(int leftPushes, int rightPushes, bool leftPresent, bool rightPresent) =>
        Advance(leftPushes, rightPushes, leftPresent, rightPresent, null);

    private bool HasBothTeams() => _participants.Any(participant => participant.Team == Team.Left) &&
        _participants.Any(participant => participant.Team == Team.Right);

    private ArenaState Advance(int leftPushes, int rightPushes, bool leftPresent, bool rightPresent,
        IReadOnlyDictionary<Guid, int>? individualPushes)
    {
        var next = State with { Tick = State.Tick + 1 };
        if (next.Mode == GameMode.AwaitingSelection || !leftPresent || !rightPresent)
        {
            if (next.Phase == Phase.Waiting)
            {
                return State = next;
            }

            return State = EnterWaiting(next);
        }

        if (next.Phase == Phase.Waiting)
        {
            return State = next with
            {
                Phase = Phase.Serving,
                ServeTicksRemaining = ServeCountdown,
            };
        }

        next = next.Mode == GameMode.IndividualPaddles
            ? next with
            {
                PlayerPaddles = [.. next.PlayerPaddles.Select(paddle => AdvanceIndividualPaddle(
                    paddle, individualPushes?.GetValueOrDefault(paddle.PlayerId) ?? 0))],
            }
            : next with
            {
                LeftPaddle = AdvancePaddle(next.LeftPaddle, leftPushes, PaddleHalfHeight),
                RightPaddle = AdvancePaddle(next.RightPaddle, rightPushes, PaddleHalfHeight),
            };

        if (next.Phase == Phase.Playing)
        {
            return State = AdvanceRally(next);
        }

        next = next with { ServeTicksRemaining = next.ServeTicksRemaining - 1 };
        if (next.ServeTicksRemaining == 0)
        {
            var angle = (_random.NextDouble() * 2 - 1) * MaxServeAngle;
            next = next with
            {
                Phase = Phase.Playing,
                Balls = [new BallState(_nextBallId++, ArenaWidth / 2, ArenaHeight / 2,
                    _serveDirection * ServeSpeed * Math.Cos(angle), ServeSpeed * Math.Sin(angle))],
            };
        }

        return State = next;
    }

    private ArenaState EnterWaiting(ArenaState arena)
    {
        _serveDirection = RandomServeDirection();
        _pickupSpawnTicks = 0;
        return arena with
        {
            Phase = Phase.Waiting,
            ServeTicksRemaining = 0,
            LeftPaddle = CenteredPaddle,
            RightPaddle = CenteredPaddle,
            PlayerPaddles = [.. arena.PlayerPaddles.Select(paddle => paddle with { Y = CenteredPaddle.Y, Velocity = 0 })],
            Balls = [CenteredBall(_nextBallId++)],
            Pickups = [],
        };
    }

    private ArenaState AdvanceRally(ArenaState arena)
    {
        var pickups = arena.Pickups.Where(pickup => pickup.RemainingTicks > 1)
            .Select(pickup => pickup with { RemainingTicks = pickup.RemainingTicks - 1 })
            .ToImmutableArray();
        var contacts = new List<PickupContact>();
        var survivors = ImmutableArray.CreateBuilder<BallState>(arena.Balls.Length);
        var leftScore = arena.LeftScore;
        var rightScore = arena.RightScore;
        foreach (var ball in arena.Balls)
        {
            var survivor = AdvanceBall(arena, ball, pickups, contacts, out var goalDirection);
            if (survivor is not null)
            {
                survivors.Add(survivor);
                continue;
            }

            _serveDirection = goalDirection;
            leftScore += goalDirection > 0 ? 1 : 0;
            rightScore += goalDirection < 0 ? 1 : 0;
        }

        var collected = new HashSet<long>();
        foreach (var contact in contacts.OrderBy(contact => contact.Time)
                     .ThenBy(contact => contact.Pickup.Id)
                     .ThenBy(contact => contact.BallId))
        {
            if (collected.Add(contact.Pickup.Id))
            {
                AddPickupBalls(survivors, contact);
            }
        }

        pickups = pickups.RemoveAll(pickup => collected.Contains(pickup.Id));
        if (++_pickupSpawnTicks == PickupSpawnInterval)
        {
            _pickupSpawnTicks = 0;
            var amount = _random.Next(1, MaxBallCount + 1);
            pickups = pickups.Add(new PickupState(_nextPickupId++,
                10 + _random.NextDouble() * 80, 3 + _random.NextDouble() * 23,
                amount, PickupLifetime));
        }

        return arena with
        {
            LeftScore = leftScore,
            RightScore = rightScore,
            Phase = survivors.Count == 0 ? Phase.Serving : Phase.Playing,
            ServeTicksRemaining = survivors.Count == 0 ? ServeCountdown : 0,
            Balls = survivors.Count == 0 ? [CenteredBall(_nextBallId++)] : survivors.ToImmutable(),
            Pickups = pickups,
        };
    }

    private void AddPickupBalls(ImmutableArray<BallState>.Builder balls, PickupContact contact)
    {
        var count = Math.Min(contact.Pickup.Amount, MaxBallCount - balls.Count);
        var direction = contact.Direction == 0 ? _serveDirection : contact.Direction;
        for (var index = 0; index < count; index++)
        {
            var angle = count == 1 ? 0 : MaxReboundAngle * (2.0 * index / (count - 1) - 1);
            balls.Add(new BallState(_nextBallId++, contact.Pickup.X, contact.Pickup.Y,
                direction * ServeSpeed * Math.Cos(angle), ServeSpeed * Math.Sin(angle)));
        }
    }

    private static BallState? AdvanceBall(ArenaState arena, BallState ball,
        ImmutableArray<PickupState> pickups, List<PickupContact> contacts, out int goalDirection)
    {
        goalDirection = 0;
        const double top = BallRadius;
        const double bottom = ArenaHeight - BallRadius;
        var (id, x, y, velocityX, velocityY) = ball;
        var remaining = TickDuration;

        while (remaining > 0)
        {
            var wallY = velocityY < 0 ? top : bottom;
            var wallTime = TimeToPlane(y, velocityY, wallY);

            var movingLeft = velocityX < 0;
            var paddleX = movingLeft ? LeftPaddleFace : RightPaddleFace;
            var paddleTime = TimeToPlane(x, velocityX, paddleX);
            var impactY = paddleTime <= remaining ? y + velocityY * paddleTime : y;
            var halfHeight = arena.Mode == GameMode.IndividualPaddles ? IndividualPaddleHalfHeight : PaddleHalfHeight;
            if (paddleTime <= remaining &&
                paddleTime <= wallTime + CoincidentContactTolerance &&
                FindPaddleCenter(arena, movingLeft ? Team.Left : Team.Right, impactY, halfHeight) is { } paddleY)
            {
                RecordPickupContacts(pickups, contacts, id, x, y, velocityX, velocityY, remaining, paddleTime);
                var wallContact = Math.Abs(paddleTime - wallTime) <= CoincidentContactTolerance;
                x = paddleX;
                y = wallContact ? wallY : impactY;
                var offset = Math.Clamp((y - paddleY) / halfHeight, -1, 1);
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
                RecordPickupContacts(pickups, contacts, id, x, y, velocityX, velocityY, remaining, goalTime);
                goalDirection = movingLeft ? -1 : 1;
                return null;
            }

            if (wallTime <= remaining)
            {
                RecordPickupContacts(pickups, contacts, id, x, y, velocityX, velocityY, remaining, wallTime);
                x += velocityX * wallTime;
                y = wallY;
                velocityY = -velocityY;
                remaining -= wallTime;
                continue;
            }

            RecordPickupContacts(pickups, contacts, id, x, y, velocityX, velocityY, remaining, remaining);
            x += velocityX * remaining;
            y += velocityY * remaining;
            break;
        }

        return new BallState(id, x, y, velocityX, velocityY);
    }

    private static double? FindPaddleCenter(ArenaState arena, Team side, double impactY, double halfHeight)
    {
        var maximumDistance = halfHeight + BallRadius;
        if (arena.Mode != GameMode.IndividualPaddles)
        {
            var center = (side == Team.Left ? arena.LeftPaddle : arena.RightPaddle).Y;
            return Math.Abs(impactY - center) <= maximumDistance ? center : null;
        }

        PlayerPaddleState? closest = null;
        var closestDistance = maximumDistance;
        foreach (var paddle in arena.PlayerPaddles)
        {
            if (paddle.Team != side)
                continue;

            var distance = Math.Abs(impactY - paddle.Y);
            if (distance < closestDistance || distance == closestDistance &&
                (closest is null || paddle.PlayerId.CompareTo(closest.PlayerId) < 0))
            {
                closest = paddle;
                closestDistance = distance;
            }
        }

        return closest?.Y;
    }

    private static void RecordPickupContacts(ImmutableArray<PickupState> pickups, List<PickupContact> contacts,
        long ballId, double x, double y, double velocityX, double velocityY, double remaining, double duration)
    {
        var speedSquared = velocityX * velocityX + velocityY * velocityY;
        foreach (var pickup in pickups)
        {
            var dx = x - pickup.X;
            var dy = y - pickup.Y;
            var distanceSquared = dx * dx + dy * dy - PickupContactRadius * PickupContactRadius;
            var contactTime = 0.0;
            if (distanceSquared > 0)
            {
                var dot = dx * velocityX + dy * velocityY;
                var discriminant = dot * dot - speedSquared * distanceSquared;
                if (dot >= 0 || discriminant < 0)
                {
                    continue;
                }

                contactTime = distanceSquared / (-dot + Math.Sqrt(discriminant));
            }

            if (contactTime <= duration)
            {
                contacts.Add(new PickupContact(TickDuration - remaining + contactTime, pickup, ballId,
                    Math.Sign(velocityX)));
            }
        }
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

    private static BallState CenteredBall(long id) => new(id, ArenaWidth / 2, ArenaHeight / 2, 0, 0);

    private readonly record struct PickupContact(double Time, PickupState Pickup, long BallId, int Direction);

    private static PlayerPaddleState AdvanceIndividualPaddle(PlayerPaddleState paddle, int pushes)
    {
        var next = AdvancePaddle(new PaddleState(paddle.Y, paddle.Velocity), pushes, IndividualPaddleHalfHeight);
        return paddle with { Y = next.Y, Velocity = next.Velocity };
    }

    private static PaddleState AdvancePaddle(PaddleState paddle, int pushes, double halfHeight)
    {
        var minY = halfHeight;
        var maxY = ArenaHeight - halfHeight;
        var velocity = Math.Clamp(paddle.Velocity + PaddleImpulse * pushes, -MaxPaddleSpeed, MaxPaddleSpeed);
        var y = Math.Clamp(paddle.Y + velocity * TickDuration, minY, maxY);
        if ((y == minY && velocity < 0) || (y == maxY && velocity > 0))
        {
            velocity = 0;
        }

        return new PaddleState(y, velocity * PaddleDecay);
    }
}
