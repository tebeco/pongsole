using Pongsole.Contracts;

namespace Pongsole.Client.Tests;

public sealed class TerminalPresentationTests
{
    [Theory]
    [InlineData("http://localhost:5187/", "http://localhost:5187/game")]
    [InlineData("http://127.0.0.2", "http://127.0.0.2/game")]
    [InlineData("http://[::1]", "http://[::1]/game")]
    [InlineData("https://example.test", "https://example.test/game")]
    public void ParsesServerAndName(string origin, string expected)
    {
        var options = ClientOptions.Parse(["--name", "Alice", "--server", origin]);

        Assert.Equal(expected, options.Endpoint!.AbsoluteUri);
        Assert.Equal("Alice", options.Name);
    }

    [Theory]
    [InlineData("http://example.test")]
    [InlineData("http://loopback")]
    [InlineData("https://user@example.test")]
    [InlineData("https://example.test/?")]
    [InlineData("https://example.test/#")]
    [InlineData("https://example.test/foo/..")]
    [InlineData("https://example.test//")]
    [InlineData("ws://localhost")]
    public void RejectsUnsupportedServerOrigins(string origin) =>
        Assert.Throws<ArgumentException>(() => ClientOptions.ParseEndpoint(origin));

    [Fact]
    public void RejectsUnknownDuplicateAndIncompleteFlags()
    {
        Assert.Throws<ArgumentException>(() => ClientOptions.Parse(["--code", "secret"]));
        Assert.Throws<ArgumentException>(() => ClientOptions.Parse(["--name", "Alice", "--name", "Bob"]));
        Assert.Throws<ArgumentException>(() => ClientOptions.Parse(["--server"]));
        Assert.Equal(new ClientOptions(null, null), ClientOptions.Parse([]));
    }

    [Theory]
    [InlineData(ConsoleKey.W, Direction.Up)]
    [InlineData(ConsoleKey.UpArrow, Direction.Up)]
    [InlineData(ConsoleKey.S, Direction.Down)]
    [InlineData(ConsoleKey.DownArrow, Direction.Down)]
    public void MapsMovement(ConsoleKey key, Direction expected) => Assert.Equal(expected, GameInput.Movement(key));

    [Theory]
    [InlineData(ConsoleKey.Q)]
    [InlineData(ConsoleKey.Escape)]
    public void ExitKeysDoNotMove(ConsoleKey key)
    {
        Assert.True(GameInput.IsExit(key));
        Assert.Null(GameInput.Movement(key));
    }

    [Fact]
    public void ArenaUsesTerminalCellAspectAndFitsMinimumWindow()
    {
        var arena = new ArenaState(0, 2, 1, Phase.Serving, 60, new PaddleState(15, 0), new PaddleState(15, 0),
            new BallState(50, 15, 0, 0));

        RosterPlayer[] players = [new("One", Team.Left), new("Two", Team.Left), new("Three", Team.Right)];
        var lines = GameRenderer.Draw(80, 24, arena, players, Team.Left, "Connected");
        var board = lines.Skip(3).TakeWhile(line => line.Length >= 61).Select(line => line.Substring(18, 43)).Where(line => line.StartsWith('|')).ToArray();

        Assert.All(lines, line => Assert.True(line.Length < 80));
        Assert.True(lines.Length < 24);
        Assert.InRange(board[0].Length / (2.0 * board.Length), 3.0, 3.6);
        Assert.Contains(board, row => row.Contains('o'));
        Assert.Contains("LEFT 2  -  1 RIGHT   |   Players 3   |   Team Left", lines);
        Assert.Contains("Serving in 1.0s", lines);
        Assert.Contains("Resize to 80x24", GameRenderer.Draw(79, 24, arena, players, Team.Left, "Connected")[0]);
        Assert.False(GameRenderer.Fits(80, 23));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Alice")]
    [InlineData("Alice ")]
    [InlineData("Alice-Bob")]
    [InlineData("Alice123456")]
    [InlineData("Élodie")]
    [InlineData("１２")]
    public void RejectsInvalidNamesWithoutTrimming(string value)
    {
        Assert.Equal(value, ClientOptions.Parse(["--name", value]).Name);
        Assert.Equal(PlayerName.ValidationMessage, Assert.Throws<ArgumentException>(() => ClientOptions.ParseName(value)).Message);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("Player0001")]
    [InlineData("0")]
    public void AcceptsAsciiNames(string value) => Assert.Equal(value, ClientOptions.ParseName(value));

    [Fact]
    public void TeamColumnsKeepAuthoritativeOrderAndReserveLastRowForOverflow()
    {
        var arena = SnapshotHistoryTests.Snapshot(0, 50, []).Arena;
        var players = new List<RosterPlayer> { new("Zed1234567", Team.Left, Up: true), new("Right1", Team.Right, Down: true) };
        players.AddRange(Enumerable.Range(1, 16).Select(index => new RosterPlayer($"Left{index}", Team.Left)));
        var lines = GameRenderer.Draw(80, 24, arena, players, Team.Right, "Connected");

        Assert.StartsWith("LEFT TEAM", lines[3]);
        Assert.Equal("RIGHT TEAM", lines[3][62..].TrimEnd());
        Assert.Equal("Zed1234567 [^] v ", lines[4][..17]);
        Assert.Equal("Right1      ^ [v]", lines[4][62..]);
        Assert.StartsWith("Left1", lines[5]);
        Assert.StartsWith("Left13 ", lines[17]);
        Assert.StartsWith("+3 more", lines[18]);
        Assert.DoesNotContain(lines, line => line.StartsWith("Left14 "));
        Assert.All(lines, line => Assert.True(line.Length <= 79));
    }
}
