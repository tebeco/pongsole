using Pongsole.Contracts;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Pongsole.Client;

internal static class GameRenderer
{
    public const int MinimumWidth = 80;
    public const int MinimumHeight = 24;
    private const int RosterWidth = PlayerName.MaximumLength + 9;
    private static readonly Style Dim = new(decoration: Decoration.Dim);
    private static readonly Style Amber = new(Color.Yellow, decoration: Decoration.Bold);
    private static readonly Style Ball = new(Color.White, decoration: Decoration.Bold);
    private static readonly Style NewBall = new(Color.Aqua, decoration: Decoration.Bold | Decoration.Underline);

    public static bool Fits(int width, int height) => width >= MinimumWidth && height >= MinimumHeight;

    public static ScreenFrame Draw(int width, int height, ArenaState? arena, IReadOnlyList<RosterPlayer> players,
        Team? team, string status, bool unicode = false, bool color = false, PresentationEffects? effects = null)
    {
        if (!Fits(width, height))
            return ScreenFrame.Plain(width, height, $"Resize to {MinimumWidth}x{MinimumHeight} (now {width}x{height})",
                status, "Q / Esc to quit");
        if (arena is null)
            return ScreenFrame.Plain(width, height, "PONGSOLE", status, "Q / Esc to quit");

        var frame = new ScreenFrame(width, height, color);
        frame.Add(new Segment($"LEFT {arena.LeftScore}", TeamStyle(Team.Left, effects?.Score(Team.Left) == true)),
            new Segment("  -  "), new Segment($"{arena.RightScore} RIGHT", TeamStyle(Team.Right, effects?.Score(Team.Right) == true)),
            new Segment($"   |   Players {players.Count}   |   Team {team?.ToString() ?? "-"}"));
        frame.Add(new Segment(status));
        if (arena.Phase == Phase.Serving)
        {
            var filled = Math.Clamp((60 - arena.ServeTicksRemaining) / 6, 0, 10);
            frame.Add(new Segment(FormattableString.Invariant($"Serving in {arena.ServeTicksRemaining / 60.0:0.0}s")),
                new Segment("  [" + new string(unicode ? '━' : '=', filled) + new string(unicode ? '─' : '-', 10 - filled) + "]", Dim));
        }
        else
        {
            frame.Add(new Segment(arena.Phase == Phase.Waiting ? "Waiting for players on both teams" : "Playing"));
        }

        var contentHeight = height - 8;
        var boardWidth = Math.Min(width - 1 - 2 * (RosterWidth + 1) - 2,
            (int)((contentHeight - 2) * (200.0 / 30)));
        var boardHeight = (int)Math.Round(boardWidth * (30.0 / 200));
        var rows = new Cell[boardHeight, boardWidth];
        for (var row = 0; row < boardHeight; row++)
            rows[row, boardWidth / 2] = new Cell(unicode ? '┊' : ':', Dim);

        foreach (var pickup in arena.Pickups) DrawPickup(rows, pickup);
        DrawPaddle(rows, 2, arena.LeftPaddle.Y, Team.Left, unicode);
        DrawPaddle(rows, 98, arena.RightPaddle.Y, Team.Right, unicode);
        foreach (var ball in arena.Balls)
        {
            var style = effects?.Ball(ball.Id) == true ? NewBall : Ball;
            rows[Scale(ball.Y, 30, boardHeight), Scale(ball.X, 100, boardWidth)] = new Cell(unicode ? '●' : 'o', style);
        }

        var left = Roster(players, Team.Left, team, contentHeight, unicode);
        var right = Roster(players, Team.Right, team, contentHeight, unicode);
        var top = (contentHeight - boardHeight - 2) / 2;
        for (var row = 0; row < contentHeight; row++)
        {
            var courtRow = row - top;
            var segments = new List<Segment>(left[row]) { Segment.Padding(1) };
            if (courtRow == 0 || courtRow == boardHeight + 1)
            {
                var upper = courtRow == 0;
                var first = unicode ? (upper ? '┌' : '└') : '+';
                var last = unicode ? (upper ? '┐' : '┘') : '+';
                segments.Add(new Segment(first + new string(unicode ? '─' : '-', boardWidth) + last, Dim));
            }
            else if (courtRow is > 0 && courtRow <= boardHeight)
            {
                var edge = unicode ? "│" : "|";
                segments.Add(new Segment(edge, Dim));
                segments.AddRange(CourtRow(rows, courtRow - 1));
                segments.Add(new Segment(edge, Dim));
            }
            else
            {
                segments.Add(Segment.Padding(boardWidth + 2));
            }

            segments.Add(Segment.Padding(1));
            segments.AddRange(right[row]);
            frame.Add([.. segments]);
        }

        frame.Add(new Segment("W/S or Up/Down to move  |  Q / Esc to quit", Dim));
        return frame;
    }

    private static IEnumerable<Segment> CourtRow(Cell[,] cells, int row)
    {
        var length = cells.GetLength(1);
        var text = new char[length];
        var start = 0;
        var previous = cells[row, 0].Style;
        for (var index = 0; index < length; index++)
        {
            var cell = cells[row, index];
            if (cell.Style != previous)
            {
                yield return new Segment(new string(text, start, index - start), previous ?? Style.Plain);
                previous = cell.Style;
                start = index;
            }
            text[index] = cell.Value == '\0' ? ' ' : cell.Value;
        }
        yield return new Segment(new string(text, start, length - start), previous ?? Style.Plain);
    }

    private static Segment[][] Roster(IReadOnlyList<RosterPlayer> players, Team team, Team? own, int height, bool unicode)
    {
        var members = players.Where(player => player.Team == team).ToArray();
        var rows = new Segment[height][];
        for (var index = 0; index < height; index++) rows[index] = [Segment.Padding(RosterWidth)];
        var style = TeamStyle(team, team == own);
        rows[0] = [new Segment((team == Team.Left ? "LEFT TEAM" : "RIGHT TEAM").PadRight(RosterWidth), style)];
        var available = height - 1;
        var visible = members.Length <= available / 2 ? members.Length : (available - 1) / 2;
        for (var index = 0; index < visible; index++)
        {
            var player = members[index];
            var latency = player.LatencyMilliseconds is int ms
                ? FormattableString.Invariant($"({ms}ms)") : unicode ? "(—)" : "(-)";
            var name = player.Name + " ";
            var latencyStyle = player.LatencyMilliseconds switch
            {
                null => Dim,
                < 150 => new Style(Color.Green),
                < 300 => new Style(Color.Yellow),
                _ => new Style(Color.Red)
            };
            rows[2 * index + 1] = [new Segment(name, style), new Segment(latency.PadRight(RosterWidth - name.Length), latencyStyle)];
            var up = unicode ? '↑' : '^';
            var down = unicode ? '↓' : 'v';
            rows[2 * index + 2] = [Segment.Padding(RosterWidth - 6),
                DirectionIcon(up, player.Up, team), DirectionIcon(down, player.Down, team)];
        }

        if (visible < members.Length)
            rows[^1] = [new Segment($"+{members.Length - visible} more".PadRight(RosterWidth), Dim)];

        return rows;
    }

    private static Segment DirectionIcon(char glyph, bool active, Team team) =>
        new(active ? $"[{glyph}]" : $" {glyph} ", active ? TeamStyle(team, true) : Dim);

    private static Style TeamStyle(Team team, bool bold = false) =>
        new(team == Team.Left ? Color.Cyan : Color.Magenta, decoration: bold ? Decoration.Bold : Decoration.None);

    private static void DrawPickup(Cell[,] rows, PickupState pickup)
    {
        var column = Scale(pickup.X, 100, rows.GetLength(1));
        var row = Math.Min(Scale(pickup.Y, 30, rows.GetLength(0)), rows.GetLength(0) - 2);
        DrawLabel(rows, row, column, FormattableString.Invariant($"+{pickup.Amount}"));
        DrawLabel(rows, row + 1, column, FormattableString.Invariant($"{Math.Ceiling(pickup.RemainingTicks / 60.0)}s"));
    }

    private static void DrawLabel(Cell[,] rows, int row, int center, string label)
    {
        var start = Math.Clamp(center - label.Length / 2, 0, rows.GetLength(1) - label.Length);
        for (var index = 0; index < label.Length; index++) rows[row, start + index] = new Cell(label[index], Amber);
    }

    private static void DrawPaddle(Cell[,] rows, double x, double y, Team team, bool unicode)
    {
        var column = Scale(x, 100, rows.GetLength(1));
        var style = TeamStyle(team, true);
        for (var row = Scale(y - 3, 30, rows.GetLength(0)); row <= Scale(y + 3, 30, rows.GetLength(0)); row++)
            rows[row, column] = new Cell(unicode ? '┃' : '|', style);
    }

    private static int Scale(double position, double extent, int cells) =>
        (int)Math.Round(Math.Clamp(position / extent, 0, 1) * (cells - 1));

    private readonly record struct Cell(char Value, Style? Style);
}

internal readonly record struct RosterPlayer(string Name, Team Team, bool Up = false, bool Down = false, int? LatencyMilliseconds = null);
