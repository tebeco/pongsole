using System.Text.Json;
using Pongsole.Contracts;

namespace Pongsole.Core.Tests;

public sealed class SharedContractTests
{
    [Fact]
    public void SnapshotUsesCamelCasePropertiesAndEnumsAndRoundTrips()
    {
        var options = new JsonSerializerOptions();
        GameJson.Configure(options);
        var initialArena = new GameEngine(42).State;
        var player = new PlayerState(Guid.NewGuid(), "Player", Team.Left, 3);
        var snapshot = new GameSnapshot(initialArena, [player]);

        var payload = JsonSerializer.Serialize(snapshot, options);
        using var document = JsonDocument.Parse(payload);
        var arena = document.RootElement.GetProperty("arena");
        var players = document.RootElement.GetProperty("players");

        Assert.Equal("waiting", arena.GetProperty("phase").GetString());
        Assert.Equal(15, arena.GetProperty("leftPaddle").GetProperty("y").GetDouble());
        Assert.Equal("left", players[0].GetProperty("team").GetString());
        Assert.Equal(3, players[0].GetProperty("lastProcessedSequence").GetInt64());
        Assert.False(players[0].TryGetProperty("resumeToken", out _));

        var roundTripped = JsonSerializer.Deserialize<GameSnapshot>(payload, options);
        Assert.NotNull(roundTripped);
        Assert.Equal(initialArena, roundTripped.Arena);
        Assert.Equal(player, Assert.Single(roundTripped.Players));
    }

    [Fact]
    public void DirectionUsesStringsAndRejectsNumericValues()
    {
        var options = new JsonSerializerOptions();
        GameJson.Configure(options);

        Assert.Equal("\"up\"", JsonSerializer.Serialize(Direction.Up, options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Direction>("0", options));
    }
}
