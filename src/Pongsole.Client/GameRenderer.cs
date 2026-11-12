using Pongsole.Contracts;

namespace Pongsole.Client;

internal static class GameRenderer
{
    public const int MinimumWidth = 80;
    public const int MinimumHeight = 24;

    public static bool Fits(int width, int height) => width >= MinimumWidth && height >= MinimumHeight;

    public static string[] Draw(int width, int height, ArenaState? arena, int playerCount, Team? team, string status)
    {
        if (!Fits(width, height))
        {
            return [$"Resize to {MinimumWidth}x{MinimumHeight} (now {width}x{height})", status, "Q / Esc to quit"];
        }

        if (arena is null)
        {
            return ["PONGSOLE", status, "Q / Esc to quit"];
        }

        // Terminal cells are approximately twice as tall as they are wide.
        var boardWidth = Math.Min(width - 3, (int)((height - 8) * (200.0 / 30)));
        var boardHeight = Math.Max(2, (int)Math.Round(boardWidth * (30.0 / 200)));
        var rows = Enumerable.Range(0, boardHeight).Select(_ => new string(' ', boardWidth).ToCharArray()).ToArray();
        DrawPaddle(rows, 2, arena.LeftPaddle.Y);
        DrawPaddle(rows, 98, arena.RightPaddle.Y);
        rows[Scale(arena.Ball.Y, 30, boardHeight)][Scale(arena.Ball.X, 100, boardWidth)] = 'o';

        var phase = arena.Phase switch
        {
            Phase.Waiting => "Waiting for players on both teams",
            Phase.Serving => FormattableString.Invariant($"Serving in {arena.ServeTicksRemaining / 60.0:0.0}s"),
            _ => "Playing"
        };

        var lines = new List<string>
        {
            $"LEFT {arena.LeftScore}  -  {arena.RightScore} RIGHT   |   Players {playerCount}   |   Team {team?.ToString() ?? "-"}",
            status,
            phase,
            "+" + new string('-', boardWidth) + "+"
        };
        lines.AddRange(rows.Select(row => "|" + new string(row) + "|"));
        lines.Add(lines[3]);
        lines.Add("W/S or Up/Down to move  |  Q / Esc to quit");
        return [.. lines];
    }

    private static void DrawPaddle(char[][] rows, double x, double y)
    {
        var column = Scale(x, 100, rows[0].Length);
        for (var row = Scale(y - 3, 30, rows.Length); row <= Scale(y + 3, 30, rows.Length); row++)
        {
            rows[row][column] = '|';
        }
    }

    private static int Scale(double position, double extent, int cells) =>
        (int)Math.Round(Math.Clamp(position / extent, 0, 1) * (cells - 1));
}
