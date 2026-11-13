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
        var initialArena = new GameEngine(42).State with
        {
            Phase = Phase.Playing,
            Balls = [new BallState(2, 50, 10, 24, 0), new BallState(3, 50, 20, -24, 0)],
            Pickups = [new PickupState(1, 30, 10, 6, 3540)],
        };
        var player = new PlayerState(Guid.NewGuid(), "Player", Team.Left, 3);
        var snapshot = new GameSnapshot(initialArena, [player]);

        var payload = JsonSerializer.Serialize(snapshot, options);
        using var document = JsonDocument.Parse(payload);
        var arena = document.RootElement.GetProperty("arena");
        var players = document.RootElement.GetProperty("players");

        Assert.Equal("playing", arena.GetProperty("phase").GetString());
        Assert.Equal(15, arena.GetProperty("leftPaddle").GetProperty("y").GetDouble());
        var balls = arena.GetProperty("balls");
        Assert.Equal(2, balls.GetArrayLength());
        Assert.Equal(2, balls[0].GetProperty("id").GetInt64());
        Assert.Equal(24, balls[0].GetProperty("velocityX").GetDouble());
        Assert.Equal(3, balls[1].GetProperty("id").GetInt64());
        Assert.False(arena.TryGetProperty("ball", out _));
        var pickup = arena.GetProperty("pickups")[0];
        Assert.Equal(6, pickup.GetProperty("amount").GetInt32());
        Assert.Equal(3540, pickup.GetProperty("remainingTicks").GetInt32());
        Assert.Equal("left", players[0].GetProperty("team").GetString());
        Assert.Equal(3, players[0].GetProperty("lastProcessedSequence").GetInt64());
        Assert.False(players[0].TryGetProperty("resumeToken", out _));

        var roundTripped = JsonSerializer.Deserialize<GameSnapshot>(payload, options);
        Assert.NotNull(roundTripped);
        Assert.Equal(initialArena with { Balls = roundTripped.Arena.Balls, Pickups = roundTripped.Arena.Pickups }, roundTripped.Arena);
        Assert.Equal<BallState>(initialArena.Balls, roundTripped.Arena.Balls);
        Assert.Equal<PickupState>(initialArena.Pickups, roundTripped.Arena.Pickups);
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
