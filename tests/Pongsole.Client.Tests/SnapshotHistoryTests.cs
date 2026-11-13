using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;

namespace Pongsole.Client.Tests;

public sealed class SnapshotHistoryTests
{
    [Fact]
    public void SameTickRefreshesMembershipAndOlderTicksCannotReplaceOrAcknowledgeIt()
    {
        var history = new SnapshotHistory(new FakeTimeProvider());
        var player = new PlayerState(Guid.NewGuid(), "Player", Team.Right, 3);
        history.Accept(Snapshot(11, 51, [player]));
        history.EstablishBaseline(new JoinResult(player.PlayerId, player.Team, "token", 3, Snapshot(10, 50, [player])));

        history.Accept(Snapshot(11, 51, [player with { LastProcessedSequence = 5 }, player with { PlayerId = Guid.NewGuid() }]));
        history.Accept(Snapshot(9, 49, [player with { LastProcessedSequence = 99 }]));

        var view = history.Read();
        Assert.Equal(11, view.Arena!.Tick);
        Assert.Equal(2, view.Players.Length);
        Assert.Equal(5, view.LastProcessed);
    }

    [Fact]
    public void InterpolatesPositionsOverOneSnapshotWhileKeepingNewestTickAndPhase()
    {
        var clock = new FakeTimeProvider();
        var history = new SnapshotHistory(clock);
        var player = new PlayerState(Guid.NewGuid(), "Player", Team.Right, 0);
        history.EstablishBaseline(new JoinResult(player.PlayerId, player.Team, "token", 0, Snapshot(7, 50, [player])));

        history.Accept(Snapshot(10, 54, [player]));
        clock.Advance(TimeSpan.FromMilliseconds(25));
        Assert.Equal(10, history.Read().Arena!.Tick);
        Assert.Equal(52, history.Read().Arena!.Ball.X);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(54, history.Read().Arena!.Ball.X);

        var serve = Snapshot(11, 50, [player]);
        history.Accept(serve with { Arena = serve.Arena with { Phase = Phase.Serving } });
        Assert.Equal(Phase.Serving, history.Read().Arena!.Phase);
        Assert.Equal(50, history.Read().Arena!.Ball.X);
    }

    [Fact]
    public void RecentDirectionsExpireIndependentlyWithoutRefreshingRepeatedEvents()
    {
        var clock = new FakeTimeProvider();
        var history = new SnapshotHistory(clock);
        var player = new PlayerState(Guid.NewGuid(), "Player", Team.Right, 0);
        history.EstablishBaseline(new JoinResult(player.PlayerId, player.Team, "token", 0, Snapshot(99, 50, [player])));

        history.Accept(Snapshot(103, 50, [player with { LastUpTick = 100 }]));
        Assert.True(Assert.Single(history.Read().Players).Up);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        history.Accept(Snapshot(109, 50, [player with { LastUpTick = 100, LastDownTick = 109 }]));
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var directions = Assert.Single(history.Read().Players);
        Assert.False(directions.Up);
        Assert.True(directions.Down);
        history.Accept(Snapshot(109, 50, [player with { LastUpTick = 100, LastDownTick = 109 }]));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.False(Assert.Single(history.Read().Players).Down);
    }

    [Fact]
    public void BaselineAndWaitingSeedPriorEventsWithoutFlashing()
    {
        var history = new SnapshotHistory(new FakeTimeProvider());
        var player = new PlayerState(Guid.NewGuid(), "Player", Team.Right, 0, LastUpTick: 8);
        history.Accept(Snapshot(10, 50, [player]));
        history.EstablishBaseline(new JoinResult(player.PlayerId, player.Team, "token", 0, Snapshot(9, 50, [player])));
        history.Accept(Snapshot(11, 50, [player]));
        Assert.False(Assert.Single(history.Read().Players).Up);

        var newer = player with { LastUpTick = 12 };
        history.Accept(Snapshot(12, 50, [newer]));
        Assert.True(Assert.Single(history.Read().Players).Up);
        Assert.False(Assert.Single(history.Read(showActivity: false).Players).Up);
        var waiting = Snapshot(13, 50, [newer]);
        history.Accept(waiting with { Arena = waiting.Arena with { Phase = Phase.Waiting } });
        Assert.False(Assert.Single(history.Read().Players).Up);
        history.Accept(Snapshot(14, 50, [newer]));
        Assert.False(Assert.Single(history.Read().Players).Up);
        history.Accept(Snapshot(15, 50, []));
        Assert.Empty(history.Read().Players);
    }

    internal static GameSnapshot Snapshot(long tick, double x, System.Collections.Immutable.ImmutableArray<PlayerState> players) =>
        new(new ArenaState(tick, 0, 0, Phase.Playing, 0, new PaddleState(15, 0), new PaddleState(15, 0),
            new BallState(x, 15, 24, 0)), players);
}
