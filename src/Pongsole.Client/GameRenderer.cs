using Pongsole.Contracts;

namespace Pongsole.Client;

internal static class GameRenderer
{
    public const int MinimumWidth = 80;
    public const int MinimumHeight = 24;
    private const int RosterWidth = PlayerName.MaximumLength + 7;

    public static bool Fits(int width, int height) => width >= MinimumWidth && height >= MinimumHeight;

    public static string[] Draw(int width, int height, ArenaState? arena, IReadOnlyList<RosterPlayer> players,
        Team? team, string status)
    {
        if (!Fits(width, height))
        {
            return [$"Resize to {MinimumWidth}x{MinimumHeight} (now {width}x{height})", status, "Q / Esc to quit"];
        }

        if (arena is null)
        {
            return ["PONGSOLE", status, "Q / Esc to quit"];
        }

        var contentHeight = height - 8;
        // Terminal cells are approximately twice as tall as they are wide.
        var boardWidth = Math.Min(width - 1 - 2 * (RosterWidth + 1) - 2,
            (int)((contentHeight - 2) * (200.0 / 30)));
        var boardHeight = (int)Math.Round(boardWidth * (30.0 / 200));
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
        var left = Roster(players, Team.Left, contentHeight);
        var right = Roster(players, Team.Right, contentHeight);
        var court = new string[contentHeight];
        var top = (contentHeight - boardHeight - 2) / 2;
        court[top] = court[top + boardHeight + 1] = "+" + new string('-', boardWidth) + "+";
        for (var row = 0; row < rows.Length; row++)
        {
            court[top + row + 1] = "|" + new string(rows[row]) + "|";
        }

        var lines = new List<string>
        {
            $"LEFT {arena.LeftScore}  -  {arena.RightScore} RIGHT   |   Players {players.Count}   |   Team {team?.ToString() ?? "-"}",
            status,
            phase,
        };
        for (var row = 0; row < contentHeight; row++)
        {
            lines.Add($"{left[row],-RosterWidth} {(court[row] ?? string.Empty).PadRight(boardWidth + 2)} {right[row],-RosterWidth}");
        }

        lines.Add("W/S or Up/Down to move  |  Q / Esc to quit");
        return lines.Select(line => line[..Math.Min(line.Length, width - 1)]).ToArray();
    }

    private static string[] Roster(IReadOnlyList<RosterPlayer> players, Team team, int height)
    {
        var members = players.Where(player => player.Team == team).ToArray();
        var lines = new string[height];
        lines[0] = team == Team.Left ? "LEFT TEAM" : "RIGHT TEAM";
        var available = height - 1;
        var visible = members.Length <= available ? members.Length : available - 1;
        for (var index = 0; index < visible; index++)
        {
            var player = members[index];
            var up = player.Up ? "[^]" : " ^ ";
            var down = player.Down ? "[v]" : " v ";
            lines[index + 1] = $"{player.Name,-PlayerName.MaximumLength} {up}{down}";
        }

        if (visible < members.Length)
        {
            lines[^1] = $"+{members.Length - visible} more";
        }

        return lines;
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

internal readonly record struct RosterPlayer(string Name, Team Team, bool Up = false, bool Down = false);
