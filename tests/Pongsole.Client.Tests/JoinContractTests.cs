using System.Text.Json;
using Pongsole.Contracts;

namespace Pongsole.Client.Tests;

public sealed class JoinContractTests
{
    [Fact]
    public void JoinResponseCarriesPrivateTokenAndSequenceForResume()
    {
        var player = new PlayerState(Guid.NewGuid(), "Player", Team.Right, 42);
        var arena = new ArenaState(
            12, 2, 1, Phase.Playing, 0, new PaddleState(15, 0), new PaddleState(15, 0),
            [new BallState(1, 50, 15, 24, 0)], []);
        var join = new JoinResult(player.PlayerId, player.Team, "private-token", 42, new GameSnapshot(arena, [player]));
        var options = new JsonSerializerOptions();
        GameJson.Configure(options);

        var json = JsonSerializer.Serialize(join, options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("private-token", root.GetProperty("resumeToken").GetString());
        Assert.Equal(42, root.GetProperty("lastProcessedSequence").GetInt64());
        Assert.DoesNotContain("private-token", root.GetProperty("snapshot").GetRawText());

        var received = JsonSerializer.Deserialize<JoinResult>(json, options);
        Assert.NotNull(received);
        Assert.Equal(player.PlayerId, received.PlayerId);
        Assert.Equal(Team.Right, received.Team);
        Assert.Equal("private-token", received.ResumeToken);
        Assert.Equal(42, received.LastProcessedSequence);
    }
}
