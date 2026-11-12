namespace Pongsole.Client;

internal interface ITerminal
{
    bool IsInteractive { get; }
    (int Width, int Height) Size { get; }
    IDisposable Begin();
    ConsoleKeyInfo? ReadKey();
    void Draw(IReadOnlyList<string> lines);
}

internal sealed class ConsoleTerminal : ITerminal
{
    private bool? cursorWasVisible;

    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public (int Width, int Height) Size => (Console.WindowWidth, Console.WindowHeight);

    public IDisposable Begin()
    {
        cursorWasVisible = OperatingSystem.IsWindows() ? Console.CursorVisible : true;
        var session = new TerminalSession(this);
        try
        {
            Console.CursorVisible = false;
            Console.Clear();
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public ConsoleKeyInfo? ReadKey() => Console.KeyAvailable ? Console.ReadKey(intercept: true) : null;

    public void Draw(IReadOnlyList<string> lines)
    {
        var (width, height) = Size;
        if (width < 2 || height < 2)
        {
            return;
        }

        try
        {
            var left = OperatingSystem.IsWindows() ? Console.WindowLeft : 0;
            var top = OperatingSystem.IsWindows() ? Console.WindowTop : 0;
            var contentWidth = width - 1;
            for (var row = 0; row < height - 1; row++)
            {
                var line = row < lines.Count ? lines[row] : string.Empty;
                Console.SetCursorPosition(left, top + row);
                Console.Write(line.AsSpan(0, Math.Min(contentWidth, line.Length)));
                if (line.Length < contentWidth)
                {
                    Console.Write(new string(' ', contentWidth - line.Length));
                }
            }
        }
        catch (ArgumentOutOfRangeException) when (Size != (width, height))
        {
            // The terminal shrank during this frame.
        }
    }

    private sealed class TerminalSession(ConsoleTerminal terminal) : IDisposable
    {
        public void Dispose()
        {
            if (terminal.cursorWasVisible is not { } visible)
            {
                return;
            }

            terminal.cursorWasVisible = null;
            Console.CursorVisible = visible;
            Console.Clear();
        }
    }
}
