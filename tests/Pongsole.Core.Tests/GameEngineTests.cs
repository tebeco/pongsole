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

    private static ArenaState CreateState() => new GameEngine(0).State with { Phase = Phase.Playing };
}
