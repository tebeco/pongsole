using Pongsole.Contracts;

namespace Pongsole.Core.Tests;

public sealed class PickupTests
{
    [Theory]
    [InlineData(1.8, true)]
    [InlineData(2.11, false)]
    public void PickupContactMatchesTheRenderedCircle(double verticalOffset, bool collects)
    {
        var engine = new GameEngine(42, CreateEngine().State with
        {
            Balls = [new BallState(1, 40, 15 + verticalOffset, 1200, 0)],
            Pickups = [new PickupState(1, 50, 15, 1, 20)],
        });

        Advance(engine, 1);

        Assert.Equal(collects ? 0 : 1, engine.State.Pickups.Length);
        Assert.Equal(collects ? 2 : 1, engine.State.Balls.Length);
    }


    [Fact]
    public void SpawnsAfterThreeHundredPlayingTicksAndExpiresBeforeTheThirteenthSpawn()
    {
        var engine = CreateEngine();

        Advance(engine, 299);
        Assert.Empty(engine.State.Pickups);
        Advance(engine, 1);
        var first = Assert.Single(engine.State.Pickups);
        Assert.Equal(1, first.Id);
        Assert.Equal(3600, first.RemainingTicks);
        Assert.InRange(first.X, 10, 90);
        Assert.InRange(first.Y, 3, 26);
        Assert.InRange(first.Amount, 1, 10);

        Advance(engine, 3599);
        Assert.Equal(12, engine.State.Pickups.Length);
        Assert.Equal(1, engine.State.Pickups[0].RemainingTicks);
        Advance(engine, 1);
        Assert.Equal(12, engine.State.Pickups.Length);
        Assert.Equal(2, engine.State.Pickups[0].Id);
        Assert.Equal(13, engine.State.Pickups[^1].Id);
        Assert.Equal(3600, engine.State.Pickups[^1].RemainingTicks);
    }

    [Fact]
    public void ServingPausesPickupLifetimeAndWaitingClearsPickupsAndRestartsSpawnClock()
    {
        var state = CreateEngine().State with
        {
            Phase = Phase.Serving,
            ServeTicksRemaining = 60,
            Pickups = [new PickupState(14, 10, 3, 2, 10)],
        };
        var engine = new GameEngine(42, state);

        Advance(engine, 60);
        Assert.Equal(10, Assert.Single(engine.State.Pickups).RemainingTicks);
        Advance(engine, 1);
        Assert.Equal(9, Assert.Single(engine.State.Pickups).RemainingTicks);
        Advance(engine, 9);
        Assert.Empty(engine.State.Pickups);

        var waiting = engine.Advance(0, 0, false, true);
        Assert.Empty(waiting.Pickups);
        Advance(engine, 61 + 299);
        Assert.Empty(engine.State.Pickups);
        Advance(engine, 1);
        Assert.Equal(15, Assert.Single(engine.State.Pickups).Id);
    }

    [Fact]
    public void PickupWithOneTickRemainingExpiresBeforeAnOverlappingBallCollectsIt()
    {
        var engine = new GameEngine(42, CreateEngine().State with
        {
            Balls = [new BallState(1, 50, 15, 0, 0)],
            Pickups = [new PickupState(1, 50, 15, 4, 1)],
        });

        Advance(engine, 1);

        Assert.Empty(engine.State.Pickups);
        Assert.Equal(1, Assert.Single(engine.State.Balls).Id);
    }

    [Fact]
    public void SweptPickupAddsItsAmountAtItsCenterAndNewBallsMoveOnTheFollowingTick()
    {
        var engine = new GameEngine(42, CreateEngine().State with
        {
            Balls = [new BallState(7, 40, 15, 1200, 0)],
            Pickups = [new PickupState(1, 50, 15, 3, 20)],
        });

        Advance(engine, 1);

        Assert.Empty(engine.State.Pickups);
        Assert.Equal(4, engine.State.Balls.Length);
        Assert.Equal(new BallState(7, 60, 15, 1200, 0), engine.State.Balls[0]);
        for (var index = 1; index <= 3; index++)
        {
            var ball = engine.State.Balls[index];
            var angle = (index - 2) * Math.PI / 3;
            Assert.Equal(7 + index, ball.Id);
            Assert.Equal(50, ball.X);
            Assert.Equal(15, ball.Y);
            Assert.Equal(24 * Math.Cos(angle), ball.VelocityX, 12);
            Assert.Equal(24 * Math.Sin(angle), ball.VelocityY, 12);
        }

        Advance(engine, 1);
        Assert.All(engine.State.Balls, ball => Assert.True(ball.X > 50));
    }

    [Fact]
    public void SweepsTheReflectedPathAndAvoidsTheChordBetweenTickEndpoints()
    {
        var engine = new GameEngine(42, CreateEngine().State with
        {
            Balls = [new BallState(1, 40, 5, 1200, -600)],
            Pickups = [new PickupState(1, 54, 3, 1, 20), new PickupState(2, 50, 5.5, 1, 20)],
        });

        Advance(engine, 1);

        Assert.Equal(2, Assert.Single(engine.State.Pickups).Id);
        Assert.Equal(new BallState(2, 54, 3, 24, 0), engine.State.Balls[1]);
    }

    [Theory]
    [InlineData(53, -24)]
    [InlineData(54, 24)]
    public void EarliestContactWinsAndEqualTimeUsesLowerBallId(double rightX, double expectedDirection)
    {
        var engine = new GameEngine(42, CreateEngine().State with
        {
            Balls = [new BallState(2, 46, 15, 600, 0), new BallState(3, rightX, 15, -600, 0)],
            Pickups = [new PickupState(1, 50, 15, 1, 20)],
        });

        Advance(engine, 1);

        Assert.Empty(engine.State.Pickups);
        Assert.Equal(3, engine.State.Balls.Length);
        Assert.Equal(expectedDirection, engine.State.Balls[2].VelocityX);
    }

    [Fact]
    public void CollectsMultiplePickupsInIdOrderForEqualTimesAndConsumesThemAtCapacity()
    {
        var engine = new GameEngine(42, CreateEngine().State with
        {
            Balls = [.. Enumerable.Range(1, 8).Select(id => new BallState(id, 0, 15, 0, 0)),
                new BallState(9, 50, 15, 24, 0)],
            Pickups = [new PickupState(2, 50, 14, 1, 20), new PickupState(1, 50, 16, 1, 20)],
        });

        Advance(engine, 1);

        Assert.Empty(engine.State.Pickups);
        Assert.Equal(10, engine.State.Balls.Length);
        Assert.Equal(new BallState(10, 50, 16, 24, 0), engine.State.Balls[^1]);
    }

    [Fact]
    public void NewbornCannotCollectAnotherPickupOnTheSameTick()
    {
        var engine = new GameEngine(42, CreateEngine().State with
        {
            Balls = [new BallState(1, 49, 15, 24, 0)],
            Pickups = [new PickupState(1, 50, 15, 1, 20), new PickupState(2, 51.9, 15, 1, 20)],
        });

        Advance(engine, 1);
        Assert.Equal(2, Assert.Single(engine.State.Pickups).Id);
        Assert.Equal(2, engine.State.Balls.Length);
        Advance(engine, 1);
        Assert.Empty(engine.State.Pickups);
        Assert.Equal(3, engine.State.Balls.Length);
    }

    [Fact]
    public void BallCanCollectThenScoreOnTheSameTickAndNewBallsKeepTheRallyGoing()
    {
        var engine = new GameEngine(42, CreateEngine().State with
        {
            Balls = [new BallState(1, 80, 10, 1500, 0)],
            Pickups = [new PickupState(1, 90, 10, 2, 20)],
        });

        Advance(engine, 1);

        Assert.Equal(1, engine.State.LeftScore);
        Assert.Equal(Phase.Playing, engine.State.Phase);
        Assert.Equal(0, engine.State.ServeTicksRemaining);
        Assert.Empty(engine.State.Pickups);
        Assert.Equal(new long[] { 2, 3 }, engine.State.Balls.Select(ball => ball.Id));
    }

    private static GameEngine CreateEngine() => new(42, new GameEngine(42).State with
    {
        Phase = Phase.Playing,
        Balls = [new BallState(1, 0, 15, 0, 0)],
    });

    private static void Advance(GameEngine engine, int ticks)
    {
        for (var tick = 0; tick < ticks; tick++)
        {
            engine.Advance(0, 0, true, true);
        }
    }
}
