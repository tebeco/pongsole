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
        Assert.Equal(2, view.PlayerCount);
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

    internal static GameSnapshot Snapshot(long tick, double x, System.Collections.Immutable.ImmutableArray<PlayerState> players) =>
        new(new ArenaState(tick, 0, 0, Phase.Playing, 0, new PaddleState(15, 0), new PaddleState(15, 0),
            new BallState(x, 15, 24, 0)), players);
}
