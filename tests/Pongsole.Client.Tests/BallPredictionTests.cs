using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;

namespace Pongsole.Client.Tests;

public sealed class BallPredictionTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData(100, 50)]
    [InlineData(1_000, 125)]
    public void UsesLocalHalfRttAndMonotonicAgeUpToStallLimit(int? latency, int lead)
    {
        var run = new Match(latency);
        var ball = new BallState(1, 40, 10, 40, 8);
        var join = run.Snapshot(1, [ball]);
        run.Join(join);
        Assert.Equal(40 + lead * .04, run.Ball.X, 8);
        run.Advance(83);
        Assert.Equal(40 + (lead + 83) * .04, run.Ball.X, 8);
        run.Advance(900);
        Assert.Equal(50, run.Ball.X, 8);
        Assert.Equal(12, run.Ball.Y, 8);
        Assert.Equal(ball, join.Arena.Balls[0]);
    }

    [Fact]
    public void SameTickRetainsGeometryLeadAndReceiptTimeWhileRefreshingMembership()
    {
        var run = new Match(100);
        run.Join(run.Snapshot(10, [new BallState(1, 50, 10, 20, 0)]));
        run.Advance(100);
        var same = run.Snapshot(10, [new BallState(1, 90, 10, 20, 0)]);
        run.History.Accept(same with { Players = [run.Player with { LatencyMilliseconds = 250, DisplayName = "Updated" }] });
        run.History.Accept(run.Snapshot(9, [new BallState(1, 80, 10, 20, 0)]));
        Assert.Equal(53, run.Ball.X, 8);
        Assert.Equal("Updated", Assert.Single(run.History.Read().Players).Name);
    }

    [Theory]
    [InlineData(96, 20, 97, 12)]
    [InlineData(97, -20, 92, 20)]
    [InlineData(98, 20, 100.5, 15)]
    [InlineData(4, -20, 3, 12)]
    [InlineData(3, 20, 8, 20)]
    [InlineData(2, -20, -0.5, 15)]
    public void StopsWholePredictionAtPaddleOrGoalWhileOutgoingBallContinues(
        double x, double vx, double expectedX, double expectedY)
    {
        var run = new Match(null);
        run.Join(run.Snapshot(1, [new BallState(1, x, 10, vx, 40)]));
        run.Advance(250);
        Assert.Equal(expectedX, run.Ball.X, 8);
        Assert.Equal(expectedY, run.Ball.Y, 8);
    }

    [Theory]
    [InlineData(600, 14)]
    [InlineData(-600, 24)]
    public void ReflectsAcrossMultipleVerticalWallCrossings(double velocityY, double expected)
    {
        var run = new Match(null);
        run.Join(run.Snapshot(1, [new BallState(1, 50, 11, 0, velocityY)]));
        run.Advance(250);
        Assert.Equal(expected, run.Ball.Y, 8);
    }

    [Fact]
    public void SurvivingBallBlendsAcrossScoreChangeWhileNewAndRemovedIdsSnap()
    {
        var run = new Match(null);
        run.Join(run.Snapshot(1, [new BallState(1, 20, 10, 20, 0), new BallState(2, 70, 10, 20, 0)]));
        run.Advance(80);
        run.History.Accept(run.Snapshot(4, [new BallState(1, 24, 10, 20, 0), new BallState(3, 74, 10, 20, 0)], score: 1));

        var balls = run.History.Read().Arena!.Balls;
        Assert.Equal(new long[] { 1, 3 }, balls.Select(ball => ball.Id));
        Assert.Equal(21.6, balls[0].X, 8);
        Assert.Equal(74, balls[1].X, 8);
        run.Advance(50);
        Assert.Equal(23.8, run.History.Read().Arena!.Balls[0].X, 8);
        run.Advance(50);
        Assert.Equal(26, run.History.Read().Arena!.Balls[0].X, 8);
    }

    [Fact]
    public void LargeCorrectionReversalPhaseAndFreshBaselineSnap()
    {
        var run = new Match(null);
        run.Join(run.Snapshot(1, [new BallState(1, 20, 10, 20, 0)]));
        run.History.Accept(run.Snapshot(2, [new BallState(1, 30, 10, 20, 0)]));
        Assert.Equal(30, run.Ball.X);
        run.History.Accept(run.Snapshot(3, [new BallState(1, 34, 10, -20, 0)]));
        Assert.Equal(34, run.Ball.X);
        run.History.Accept(run.Snapshot(4, [new BallState(1, 33, 10, -20, 0)], Phase.Serving));
        run.Advance(150);
        Assert.Equal(33, run.Ball.X);
        run.History.Accept(run.Snapshot(5, [new BallState(1, 36, 10, -20, 0)]));
        Assert.Equal(36, run.Ball.X);
        run.Join(run.Snapshot(1, [new BallState(1, 38, 10, -20, 0)]));
        Assert.Equal(38, run.Ball.X);
    }

    [Fact]
    public void CorrectionCannotDisplayBallPastApproachedFace()
    {
        var run = new Match(null);
        run.Join(run.Snapshot(1, [new BallState(1, 98, 10, 20, 0)]));
        run.History.Accept(run.Snapshot(2, [new BallState(1, 96, 10, 20, 0)]));
        Assert.Equal(97, run.Ball.X);
    }

    [Fact]
    public void RetainsPickupsUntilAuthoritativeTickRemovesThem()
    {
        var run = new Match(100);
        var ball = new BallState(1, 50, 15, 20, 0);
        var initial = run.Snapshot(1, [ball]);
        var pickup = new PickupState(7, 52, 15, 10, 61);
        run.Join(initial with { Arena = initial.Arena with { Pickups = [pickup] } });

        run.Advance(500);
        Assert.Equal(pickup, Assert.Single(run.History.Read().Arena!.Pickups));
        run.History.Accept(initial);
        Assert.Equal(pickup, Assert.Single(run.History.Read().Arena!.Pickups));
        run.History.Accept(run.Snapshot(2, [ball]));
        Assert.Empty(run.History.Read().Arena!.Pickups);
    }

    private sealed class Match
    {
        private readonly FakeTimeProvider clock = new();
        public SnapshotHistory History { get; }
        public PlayerState Player { get; }

        public Match(int? latency)
        {
            History = new SnapshotHistory(clock);
            Player = new PlayerState(Guid.NewGuid(), "Player", Team.Left, 0, LatencyMilliseconds: latency);
        }
        public BallState Ball => Assert.Single(History.Read().Arena!.Balls);

        public GameSnapshot Snapshot(long tick, System.Collections.Immutable.ImmutableArray<BallState> balls,
            Phase phase = Phase.Playing, int score = 0)
        {
            var snapshot = SnapshotHistoryTests.Snapshot(tick, 50, [Player,
                new PlayerState(Guid.NewGuid(), "Other", Team.Right, 0, LatencyMilliseconds: 220)]);
            return snapshot with { Arena = snapshot.Arena with { Balls = balls, Phase = phase, LeftScore = score } };
        }

        public void Join(GameSnapshot snapshot) =>
            History.EstablishBaseline(new JoinResult(Player.PlayerId, Team.Left, "token", 0, snapshot));

        public void Advance(int milliseconds) => clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
    }
}
