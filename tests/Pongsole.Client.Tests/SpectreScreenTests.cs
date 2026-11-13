using Microsoft.Extensions.Time.Testing;
using Pongsole.Contracts;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pongsole.Client.Tests;

public sealed class SpectreScreenTests
{
    [Theory]
    [InlineData(true, false, AnsiSupport.Yes, ColorSystemSupport.TrueColor)]
    [InlineData(true, true, AnsiSupport.Yes, ColorSystemSupport.NoColors)]
    [InlineData(false, false, AnsiSupport.Detect, ColorSystemSupport.Detect)]
    [InlineData(false, true, AnsiSupport.Detect, ColorSystemSupport.NoColors)]
    public void AdvertisesKnownDashboardCapabilitiesAndRespectsNoColor(
        bool dashboard, bool noColor, AnsiSupport ansi, ColorSystemSupport colors)
    {
        var settings = ConsoleTerminal.ScreenSettings(dashboard, noColor);
        Assert.Equal(ansi, settings.Ansi);
        Assert.Equal(colors, settings.ColorSystem);
    }

    [Theory]
    [InlineData(80, 24, false, false, 39)]
    [InlineData(80, 24, true, true, 39)]
    [InlineData(120, 35, true, true, 79)]
    public void BoundsMaximalRosterAndMultipleObjects(int width, int height, bool unicode, bool color, int courtWidth)
    {
        var arena = SnapshotHistoryTests.Snapshot(5, 50, []).Arena with
        {
            Balls = [.. Enumerable.Range(0, 10).Select(index => new BallState(index + 1, 5 + index * 10, 0.5, 0, 0))],
            Pickups = [.. Enumerable.Range(0, 12).Select(index => new PickupState(index + 1, 5 + index % 6 * 17,
                index < 6 ? 12 : 24, index % 10 + 1, 3600))]
        };
        RosterPlayer[] players = [.. Enumerable.Range(0, 64)
            .Select(index => new RosterPlayer($"Player{index:D4}", index < 32 ? Team.Left : Team.Right,
                Up: true, LatencyMilliseconds: index == 0 ? 2000 : 60))];
        var console = MakeConsole(new StringWriter(), width, height);
        var frame = GameRenderer.Draw(width, height, arena, players, Team.Left, "Connected", unicode, color);
        var segments = frame.GetSegments(console).ToArray();
        var rows = frame.Lines;

        Assert.All(rows, row => Assert.True(new Segment(row).CellCount() <= width - 1));
        Assert.True(rows.Length <= height - 1);
        Assert.Contains(rows, row => row.Contains("Player0000 (2000ms)"));
        Assert.Contains(rows, row => row.Contains("+") && row.Contains("more"));
        Assert.Contains(rows, row => row.Contains((unicode ? "┌" : "+") + new string(unicode ? '─' : '-', courtWidth - 2)));
        Assert.Equal(10, rows.Sum(row => row.Length >= 20 + courtWidth
            ? row.Substring(20, courtWidth).Count(character => character == (unicode ? '●' : 'o')) : 0));
        Assert.Contains(rows, row => row.Contains("60s"));
        Assert.Contains(segments, segment => segment.Text.Contains("LEFT 0") && segment.Style.Foreground == (color ? Color.Cyan : Color.Default));
        Assert.Contains(segments, segment => segment.Text.Contains("(2000ms)") && segment.Style.Foreground == (color ? Color.Red : Color.Default));
        Assert.Contains(segments, segment => segment.Text.Contains(unicode ? "[↑]" : "[^]") &&
            segment.Style.Foreground == (color ? Color.Cyan : Color.Default));
        if (!unicode) Assert.All(rows, row => Assert.All(row, character => Assert.True(char.IsAscii(character))));
    }

    [Fact]
    public void ClipsWideTextAtCellBoundariesAndDoesNotInterpretMarkup()
    {
        using var writer = new StringWriter();
        var console = MakeConsole(writer, 12, 4);
        var frame = ScreenFrame.Plain(12, 4, "[red]literal[/]", "1234567890界", "third", "fourth");
        console.Write(frame);

        Assert.Equal("[red]litera", frame.Lines[0]);
        Assert.Equal("1234567890", frame.Lines[1]);
        Assert.Equal(3, frame.Lines.Length);
        Assert.Contains("[red]litera", writer.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("quit")]
    [InlineData("cancel")]
    [InlineData("error")]
    public async Task LiveScreenRestoresCursorOnceAcrossExitPaths(string? failure)
    {
        using var writer = new StringWriter();
        var console = MakeConsole(writer, 80, 24);
        var terminal = new ConsoleTerminal(console, () => (80, 24));
        var run = terminal.RunScreenAsync(() =>
        {
            terminal.Draw(ScreenFrame.Plain(80, 24, "Connected"));
            return failure switch
            {
                "quit" => throw new ClientExit(),
                "cancel" => throw new OperationCanceledException(),
                "error" => throw new IOException(),
                _ => Task.CompletedTask
            };
        });

        if (failure is null) await run;
        else await Assert.ThrowsAnyAsync<Exception>(() => run);
        var output = writer.ToString();
        Assert.Equal(1, Count(output, "\u001b[?25l"));
        Assert.Equal(1, Count(output, "\u001b[?25h"));
        Assert.Throws<InvalidOperationException>(() => terminal.Draw(ScreenFrame.Plain(80, 24, "outside")));
    }

    [Fact]
    public void ScoreAndBallEffectsExpireWithoutNewSnapshotsAndResetOnReconnect()
    {
        var clock = new FakeTimeProvider();
        var effects = new PresentationEffects(clock);
        var arena = SnapshotHistoryTests.Snapshot(1, 50, []).Arena;
        effects.Observe(arena);
        Assert.False(effects.Ball(1));
        var changed = arena with { Tick = 2, LeftScore = 1, Balls = [.. arena.Balls, new BallState(2, 30, 10, 0, 0)] };
        effects.Observe(changed);
        Assert.True(effects.Score(Team.Left));
        Assert.False(effects.Score(Team.Right));
        Assert.True(effects.Ball(2));
        Assert.False(effects.Ball(1));

        clock.Advance(TimeSpan.FromMilliseconds(240));
        effects.Observe(changed);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.False(effects.Ball(2));
        Assert.True(effects.Score(Team.Left));
        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(effects.Score(Team.Left));

        effects.Observe(changed with { Tick = 3, RightScore = 1 });
        Assert.True(effects.Score(Team.Right));
        effects.Observe(arena);
        Assert.False(effects.Score(Team.Right));
        Assert.False(effects.Ball(1));
        Assert.False(effects.Ball(2));
    }

    private static IAnsiConsole MakeConsole(StringWriter writer, int width, int height)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes, ColorSystem = ColorSystemSupport.TrueColor, Interactive = InteractionSupport.Yes,
            Out = new AnsiConsoleOutput(writer)
        });
        console.Profile.Width = width;
        console.Profile.Height = height;
        return console;
    }

    private static int Count(string value, string needle) => (value.Length - value.Replace(needle, "").Length) / needle.Length;
}
