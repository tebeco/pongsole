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
    private (int Width, int Height) lastSize;
    private string[] lastFrame = [];

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
            if (lastSize != (width, height))
            {
                Console.Clear();
                lastFrame = [];
                lastSize = (width, height);
            }

            var left = OperatingSystem.IsWindows() ? Console.WindowLeft : 0;
            var top = OperatingSystem.IsWindows() ? Console.WindowTop : 0;
            var contentWidth = width - 1;
            var frame = lines.Take(height - 1).ToArray();
            for (var row = 0; row < Math.Max(frame.Length, lastFrame.Length); row++)
            {
                var line = row < frame.Length ? frame[row] : string.Empty;
                if (row < lastFrame.Length && line == lastFrame[row])
                {
                    continue;
                }

                Console.SetCursorPosition(left, top + row);
                Console.Write(line.AsSpan(0, Math.Min(contentWidth, line.Length)));
                if (line.Length < contentWidth)
                {
                    Console.Write(new string(' ', contentWidth - line.Length));
                }
            }

            lastFrame = frame;
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IOException && Size != (width, height))
        {
            // The terminal shrank during this frame. Redraw on the next frame.
            lastSize = default;
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
