using Pongsole.Contracts;

namespace Pongsole.Core.Tests;

public sealed class GameModeTests
{
    private static readonly Guid First = Guid.Parse("00000001-0000-0000-0000-000000000000");
    private static readonly Guid Second = Guid.Parse("01000000-0000-0000-0000-000000000000");
    private static readonly Guid Opponent = Guid.Parse("ffffffff-0000-0000-0000-000000000000");
    private static readonly PaddleParticipant LeftFirst = new(First, Team.Left);
    private static readonly PaddleParticipant LeftSecond = new(Second, Team.Left);
    private static readonly PaddleParticipant Right = new(Opponent, Team.Right);

    [Fact]
    public void AwaitingSelectionStaysWaitingWithBothTeamsPresent()
    {
        var engine = new GameEngine(42);
        engine.SynchronizeParticipants(GameMode.AwaitingSelection, [LeftFirst, Right]);
        for (var tick = 0; tick < 90; tick++)
            engine.Advance([new PlayerPaddleInput(First, 1)]);

        Assert.Equal(Phase.Waiting, engine.State.Phase);
        Assert.Empty(engine.State.PlayerPaddles);
        Assert.Equal(15, engine.State.LeftPaddle.Y);
        engine.SynchronizeParticipants(GameMode.AllTogether, [LeftFirst, Right]);
        Assert.Equal(60, engine.Advance([]).ServeTicksRemaining);
    }

    [Fact]
    public void PersonalPaddlesIntegrateAttributedPushesAndSynchronizeMembership()
    {
        var engine = new GameEngine(42);
        engine.SynchronizeParticipants(GameMode.IndividualPaddles, [LeftSecond, Right, LeftFirst]);
        engine.Advance([]);
        var moved = engine.Advance([new PlayerPaddleInput(First, -100), new PlayerPaddleInput(Second, 1),
            new PlayerPaddleInput(Guid.Empty, 100)]);
        Assert.Equal(13.8, Paddle(moved, First).Y, 12);
        Assert.Equal(-72 * Math.Pow(2, -1.0 / 21), Paddle(moved, First).Velocity, 12);
        Assert.Equal(15.12, Paddle(moved, Second).Y, 12);
        Assert.Equal(15, Paddle(moved, Opponent).Y);

        engine.SynchronizeParticipants(GameMode.IndividualPaddles, [LeftFirst, Right]);
        Assert.Equal(Paddle(moved, First), Paddle(engine.State, First));
        Assert.DoesNotContain(engine.State.PlayerPaddles, paddle => paddle.PlayerId == Second);
        engine.SynchronizeParticipants(GameMode.IndividualPaddles, [LeftFirst, LeftSecond, Right]);
        Assert.Equal(15, Paddle(engine.State, Second).Y);

        for (var tick = 0; tick < 20; tick++)
            engine.Advance([new PlayerPaddleInput(First, -100), new PlayerPaddleInput(Second, 100)]);
        Assert.Equal(new PlayerPaddleState(First, Team.Left, 1.5, 0), Paddle(engine.State, First));
        Assert.Equal(new PlayerPaddleState(Second, Team.Left, 28.5, 0), Paddle(engine.State, Second));
    }

    [Theory]
    [InlineData(15, 1)]
    [InlineData(15.2, -1)]
    public void ClosestPaddleWinsAndExactTieUsesRfcGuidOrder(double ballY, int verticalDirection)
    {
        var state = new GameEngine(42).State with
        {
            Mode = GameMode.IndividualPaddles,
            Phase = Phase.Playing,
            PlayerPaddles = [new(Second, Team.Left, 16, 0), new(First, Team.Left, 14, 0), new(Opponent, Team.Right, 15, 0)],
            Balls = [new BallState(1, 3.2, ballY, -24, 0)],
        };
        var engine = new GameEngine(42, state);
        engine.SynchronizeParticipants(GameMode.IndividualPaddles, [LeftSecond, Right, LeftFirst]);

        var ball = Assert.Single(engine.Advance([]).Balls);

        Assert.True(ball.VelocityX > 0);
        Assert.Equal(verticalDirection, Math.Sign(ball.VelocityY));
        Assert.Equal(25.2, double.Hypot(ball.VelocityX, ball.VelocityY), 12);
    }

    [Fact]
    public void BothPersonalPaddlesCollideAndTeamLossClearsRallyWithoutScores()
    {
        var state = new GameEngine(42).State with
        {
            LeftScore = 5,
            RightScore = 7,
            Mode = GameMode.IndividualPaddles,
            Phase = Phase.Playing,
            PlayerPaddles = [new(First, Team.Left, 8, 0), new(Second, Team.Left, 22, 0), new(Opponent, Team.Right, 15, 0)],
            Balls = [new BallState(1, 4, 8, -120, 0), new BallState(2, 4, 22, -120, 0)],
            Pickups = [new PickupState(1, 50, 15, 5, 300)],
        };
        var engine = new GameEngine(42, state);
        engine.SynchronizeParticipants(GameMode.IndividualPaddles, [LeftFirst, LeftSecond, Right]);
        Assert.All(engine.Advance([]).Balls, ball => Assert.Equal(45, ball.VelocityX));

        engine.SynchronizeParticipants(GameMode.IndividualPaddles, [LeftFirst, LeftSecond]);
        Assert.Equal(Phase.Waiting, engine.State.Phase);
        Assert.Equal(5, engine.State.LeftScore);
        Assert.Equal(7, engine.State.RightScore);
        Assert.Empty(engine.State.Pickups);
        Assert.Equal(0, Assert.Single(engine.State.Balls).VelocityX);
        Assert.All(engine.State.PlayerPaddles, paddle => Assert.Equal(15, paddle.Y));
    }

    private static PlayerPaddleState Paddle(ArenaState state, Guid id) =>
        Assert.Single(state.PlayerPaddles, paddle => paddle.PlayerId == id);
}
