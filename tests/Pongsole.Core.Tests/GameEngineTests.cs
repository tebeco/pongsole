using Pongsole.Contracts;

namespace Pongsole.Core.Tests;

public sealed class GameEngineTests
{
    [Theory]
    [InlineData(1, -1, 2, -2)]
    [InlineData(3, -4, 6, -8)]
    [InlineData(1 - 1, 2 - 2, 0, 0)]
    [InlineData(int.MaxValue, int.MinValue, 20, -20)]
    public void AppliesNetPushesBeforeIntegrationAndDecay(int left, int right, double leftSpeed, double rightSpeed)
    {
        var engine = new GameEngine(42, CreateState());

        var next = engine.Advance(left, right, true, true);

        Assert.Equal(15 + leftSpeed / 60, next.LeftPaddle.Y, 12);
        Assert.Equal(15 + rightSpeed / 60, next.RightPaddle.Y, 12);
        Assert.Equal(leftSpeed * Math.Pow(2, -1.0 / 21), next.LeftPaddle.Velocity, 12);
        Assert.Equal(rightSpeed * Math.Pow(2, -1.0 / 21), next.RightPaddle.Velocity, 12);
    }

    [Fact]
    public void MoreFrequentSameDirectionPushesBuildSpeedBeforeSaturation()
    {
        var engine = new GameEngine(42, CreateState());

        for (var tick = 0; tick < 60; tick++)
        {
            var faster = tick % 6 == 5 ? 1 : 0;
            var slower = tick % 20 == 19 ? 1 : 0;
            engine.Advance(faster, slower, true, true);
        }

        var state = engine.State;
        Assert.True(state.RightPaddle.Velocity > 0);
        Assert.True(state.LeftPaddle.Velocity > state.RightPaddle.Velocity);
        Assert.True(state.LeftPaddle.Velocity < 20);
    }

    [Fact]
    public void HalvesUnconstrainedVelocityAfterTwentyOneTicks()
    {
        var engine = new GameEngine(42, CreateState() with { LeftPaddle = new PaddleState(15, 10) });

        for (var tick = 0; tick < 21; tick++)
        {
            engine.Advance(0, 0, true, true);
        }

        Assert.Equal(5, engine.State.LeftPaddle.Velocity, 12);
    }

    [Fact]
    public void ClampsPaddlesAndClearsOutwardVelocity()
    {
        var engine = new GameEngine(42, CreateState() with
        {
            LeftPaddle = new PaddleState(3.01, 0),
            RightPaddle = new PaddleState(26.99, 0),
        });

        var clamped = engine.Advance(-1, 1, true, true);
        Assert.Equal(new PaddleState(3, 0), clamped.LeftPaddle);
        Assert.Equal(new PaddleState(27, 0), clamped.RightPaddle);

        var inward = engine.Advance(1, -1, true, true);
        Assert.True(inward.LeftPaddle.Y > 3);
        Assert.True(inward.RightPaddle.Y < 27);
    }

    [Fact]
    public void StartsSixtyTickCountdownAndLaunchesFromCenter()
    {
        var engine = new GameEngine(42);
        var initial = engine.State;

        var started = engine.Advance(0, 0, true, true);
        Assert.Equal(1, started.Tick);
        Assert.Equal(Phase.Waiting, initial.Phase);
        Assert.Equal(Phase.Serving, started.Phase);
        Assert.Equal(60, started.ServeTicksRemaining);

        for (var remaining = 59; remaining > 0; remaining--)
        {
            var serving = engine.Advance(1, 0, true, true);
            Assert.Equal(Phase.Serving, serving.Phase);
            Assert.Equal(remaining, serving.ServeTicksRemaining);
            Assert.Equal(initial.Ball, serving.Ball);
        }

        var launched = engine.Advance(0, 0, true, true);
        Assert.Equal(61, launched.Tick);
        Assert.Equal(Phase.Playing, launched.Phase);
        Assert.Equal(0, launched.ServeTicksRemaining);
        Assert.Equal(50, launched.Ball.X);
        Assert.Equal(15, launched.Ball.Y);
        Assert.Equal(24, double.Hypot(launched.Ball.VelocityX, launched.Ball.VelocityY), 12);
        Assert.InRange(Math.Abs(launched.Ball.VelocityY), 0, 12);
        Assert.True(launched.LeftPaddle.Y > 15);
    }

    [Fact]
    public void WaitingCentersArenaAndRetainsScoresUntilBothTeamsReturn()
    {
        var engine = new GameEngine(42, CreateState() with
        {
            LeftScore = 3,
            RightScore = 5,
            LeftPaddle = new PaddleState(20, 10),
            RightPaddle = new PaddleState(10, -10),
            Ball = new BallState(67, 12, 20, 4),
        });

        var waiting = engine.Advance(10, -10, true, false);
        Assert.Equal(Phase.Waiting, waiting.Phase);
        Assert.Equal(0, waiting.ServeTicksRemaining);
        Assert.Equal(3, waiting.LeftScore);
        Assert.Equal(5, waiting.RightScore);
        Assert.Equal(new PaddleState(15, 0), waiting.LeftPaddle);
        Assert.Equal(new PaddleState(15, 0), waiting.RightPaddle);
        Assert.Equal(new BallState(50, 15, 0, 0), waiting.Ball);

        Assert.Equal(Phase.Waiting, engine.Advance(0, 0, false, true).Phase);
        var restarted = engine.Advance(0, 0, true, true);
        Assert.Equal(waiting with { Phase = Phase.Serving, ServeTicksRemaining = 60, Tick = 3 }, restarted);

        for (var tick = 0; tick < 60; tick++)
        {
            engine.Advance(0, 0, true, true);
        }

        Assert.Equal(Phase.Playing, engine.State.Phase);
        Assert.Equal(3, engine.State.LeftScore);
        Assert.Equal(5, engine.State.RightScore);
        Assert.Equal(50, engine.State.Ball.X);
        Assert.Equal(24, double.Hypot(engine.State.Ball.VelocityX, engine.State.Ball.VelocityY), 12);
    }

    [Fact]
    public void ReplaysSeedAndInputsAcrossPresenceChanges()
    {
        var first = new GameEngine(42);
        var second = new GameEngine(42);

        for (var tick = 0; tick < 800; tick++)
        {
            var left = tick % 5 - 2;
            var right = tick % 3 - 1;
            var rightPresent = tick % 200 > 5;
            Assert.Equal(first.Advance(left, right, true, rightPresent), second.Advance(left, right, true, rightPresent));
        }
    }

    [Theory]
    [InlineData(4, -120, 1)]
    [InlineData(96, 120, -1)]
    public void SweepsAcrossBothPaddleFacesAndCapsReboundSpeed(
        double x, double velocityX, int direction)
    {
        var engine = new GameEngine(42, CreateState() with { Ball = new BallState(x, 15, velocityX, 0) });

        var next = engine.Advance(0, 0, true, true);

        Assert.Equal(Phase.Playing, next.Phase);
        Assert.Equal(direction * 45, next.Ball.VelocityX, 12);
        Assert.Equal(0, next.Ball.VelocityY, 12);
        Assert.Equal(x - direction * 0.625, next.Ball.X, 12);
    }

    [Theory]
    [InlineData(18, 1)]
    [InlineData(11.6, -1)]
    public void ReboundsAtSixtyDegreesAtAndBeyondPaddleEdge(double y, int verticalDirection)
    {
        var engine = new GameEngine(42, CreateState() with { Ball = new BallState(3.2, y, -24, 0) });

        var next = engine.Advance(0, 0, true, true);

        Assert.Equal(25.2, double.Hypot(next.Ball.VelocityX, next.Ball.VelocityY), 12);
        Assert.Equal(verticalDirection * Math.PI / 3, Math.Atan2(next.Ball.VelocityY, next.Ball.VelocityX), 12);
    }

    [Fact]
    public void UsesUpdatedPaddlePositionForImpact()
    {
        var engine = new GameEngine(42, CreateState() with { Ball = new BallState(3.2, 18.6, -24, 0) });

        var next = engine.Advance(10, 0, true, true);

        Assert.True(next.Ball.VelocityX > 0);
    }

    [Fact]
    public void ReflectsFromMultipleWallsWithoutLosingSpeed()
    {
        var engine = new GameEngine(42, CreateState() with { Ball = new BallState(50, 15, 0, 6000) });

        var next = engine.Advance(0, 0, true, true);

        Assert.Equal(50, next.Ball.X, 12);
        Assert.Equal(2, next.Ball.Y, 12);
        Assert.Equal(0, next.Ball.VelocityX, 12);
        Assert.Equal(-6000, next.Ball.VelocityY, 12);
    }

    [Theory]
    [InlineData(0.6, 0, 0.6)]
    [InlineData(1.5, -100, 0.5)]
    [InlineData(1.5, -200, 1.5)]
    public void ResolvesWallAndPaddleContactsInTimeOrder(double initialY, double velocityY, double impactY)
    {
        var engine = new GameEngine(42, CreateState() with
        {
            LeftPaddle = new PaddleState(3, 0),
            Ball = new BallState(4, initialY, -100, velocityY),
        });

        var next = engine.Advance(0, 0, true, true);

        var angle = (3 - impactY) / 3 * Math.PI / 3;
        var expectedX = 3 + 45 * Math.Cos(angle) * (1.0 / 60 - 0.01);
        var expectedY = impactY - 45 * Math.Sin(angle) * (1.0 / 60 - 0.01);
        Assert.Equal(expectedX, next.Ball.X, 12);
        Assert.Equal(0.5 + Math.Abs(expectedY - 0.5), next.Ball.Y, 12);
        Assert.Equal(45, double.Hypot(next.Ball.VelocityX, next.Ball.VelocityY), 12);
        Assert.Equal(Math.CopySign(45 * Math.Sin(angle), 0.5 - expectedY), next.Ball.VelocityY, 12);
    }

    [Theory]
    [InlineData(4, -300, 0, 1)]
    [InlineData(96, 300, 1, 0)]
    public void AGoalScoresOnceAndTheNextServeTargetsTheConcedingTeam(
        double x, double velocityX, long leftScore, long rightScore)
    {
        var engine = new GameEngine(42, CreateState() with { Ball = new BallState(x, 10, velocityX, 0) });

        var scored = engine.Advance(1, -1, true, true);

        Assert.Equal(leftScore, scored.LeftScore);
        Assert.Equal(rightScore, scored.RightScore);
        Assert.Equal(new BallState(50, 15, 0, 0), scored.Ball);
        Assert.Equal(Phase.Serving, scored.Phase);
        Assert.Equal(60, scored.ServeTicksRemaining);
        Assert.True(scored.LeftPaddle.Y > 15);
        Assert.True(scored.RightPaddle.Y < 15);
        Assert.True(scored.LeftPaddle.Velocity > 0);
        Assert.True(scored.RightPaddle.Velocity < 0);

        for (var tick = 0; tick < 60; tick++)
        {
            engine.Advance(0, 0, true, true);
        }

        var launched = engine.State;
        Assert.Equal(leftScore, launched.LeftScore);
        Assert.Equal(rightScore, launched.RightScore);
        Assert.Equal(Phase.Playing, launched.Phase);
        Assert.Equal(50, launched.Ball.X);
        Assert.Equal(Math.Sign(velocityX), Math.Sign(launched.Ball.VelocityX));

        var moved = engine.Advance(0, 0, true, true);
        Assert.Equal(50 + launched.Ball.VelocityX / 60, moved.Ball.X, 12);
        Assert.Equal(15 + launched.Ball.VelocityY / 60, moved.Ball.Y, 12);
    }

    [Fact]
    public void BehindThePaddleBallWaitsUntilItFullyExitsToScore()
    {
        var engine = new GameEngine(42, CreateState() with { Ball = new BallState(2.8, 15, -60, 0) });

        var partiallyOutside = engine.Advance(0, 0, true, true);
        partiallyOutside = engine.Advance(0, 0, true, true);
        partiallyOutside = engine.Advance(0, 0, true, true);
        Assert.Equal(Phase.Playing, partiallyOutside.Phase);
        Assert.Equal(-0.2, partiallyOutside.Ball.X, 12);

        var scored = engine.Advance(0, 0, true, true);
        Assert.Equal(1, scored.RightScore);
    }

    [Fact]
    public void ScoresWhenBallReachesExitAtTickEnd()
    {
        var engine = new GameEngine(42, CreateState() with { Ball = new BallState(99.5, 15, 60, 0) });

        var scored = engine.Advance(0, 0, true, true);

        Assert.Equal(1, scored.LeftScore);
        Assert.Equal(60, scored.ServeTicksRemaining);
    }

    [Fact]
    public void CoincidentPaddleAndWallAtTickEndLeavesBallMovingInward()
    {
        var engine = new GameEngine(42, CreateState() with
        {
            LeftPaddle = new PaddleState(3, 0),
            Ball = new BallState(4, 1.5, -60, -60),
        });

        var next = engine.Advance(0, 0, true, true);

        Assert.Equal(3, next.Ball.X);
        Assert.Equal(0.5, next.Ball.Y);
        Assert.True(next.Ball.VelocityX > 0);
        Assert.True(next.Ball.VelocityY > 0);
    }

    private static ArenaState CreateState() => new GameEngine(0).State with { Phase = Phase.Playing };
}
