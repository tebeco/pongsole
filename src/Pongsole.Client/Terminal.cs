using Spectre.Console;

namespace Pongsole.Client;

internal interface ITerminal
{
    bool IsInteractive { get; }
    (int Width, int Height) Size { get; }
    bool Unicode { get; }
    bool Color { get; }
    Task RunScreenAsync(Func<Task> action);
    ConsoleKeyInfo? ReadKey();
    void Draw(Spectre.Console.Rendering.IRenderable frame);
}

internal sealed class ConsoleTerminal : ITerminal
{
    private readonly IAnsiConsole console;
    private readonly Func<(int Width, int Height)> size;
    private LiveDisplayContext? live;
    private readonly bool preserveWindowsCursor;

    public ConsoleTerminal() : this(CreateConsole(), () => (Console.WindowWidth, Console.WindowHeight))
    {
        preserveWindowsCursor = OperatingSystem.IsWindows();
    }

    internal ConsoleTerminal(IAnsiConsole console, Func<(int Width, int Height)> size)
    {
        this.console = console;
        this.size = size;
    }

    private static IAnsiConsole CreateConsole() => AnsiConsole.Create(ScreenSettings(
        Environment.GetEnvironmentVariable("PONGSOLE_DASHBOARD_TERMINAL") == "1",
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))));

    internal static AnsiConsoleSettings ScreenSettings(bool dashboard, bool noColor) => new()
    {
        Ansi = dashboard ? AnsiSupport.Yes : AnsiSupport.Detect,
        ColorSystem = noColor ? ColorSystemSupport.NoColors :
            dashboard ? ColorSystemSupport.TrueColor : ColorSystemSupport.Detect
    };

    public bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    public (int Width, int Height) Size => size();
    public bool Unicode => console.Profile.Capabilities.Unicode;
    public bool Color => console.Profile.Capabilities.ColorSystem != ColorSystem.NoColors;

    public async Task RunScreenAsync(Func<Task> action)
    {
        // Spectre restores a visible cursor on exit. Preserve a Windows cursor that began hidden.
        var wasHidden = OperatingSystem.IsWindows() && preserveWindowsCursor && !Console.CursorVisible;
        try
        {
            await console.Live(ScreenFrame.Plain(1, 1)).AutoClear(true).Overflow(VerticalOverflow.Crop)
                .StartAsync(async context =>
                {
                    live = context;
                    try { await action(); }
                    finally { live = null; }
                });
        }
        finally
        {
            if (wasHidden) console.Cursor.Hide();
        }
    }

    public ConsoleKeyInfo? ReadKey() => Console.KeyAvailable ? Console.ReadKey(intercept: true) : null;

    public void Draw(Spectre.Console.Rendering.IRenderable frame)
    {
        var context = live ?? throw new InvalidOperationException("The terminal screen is not running.");
        context.UpdateTarget(frame);
        context.Refresh();
    }
}
