namespace Pongsole.Client;

internal sealed class TerminalPrompt(ITerminal terminal, TimeProvider timeProvider)
{
    public async Task<string> ReadAsync(string label, bool masked, CancellationToken cancellationToken)
    {
        var value = new System.Text.StringBuilder();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / 30), timeProvider);

        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (terminal.ReadKey() is { } key)
            {
                switch (key.Key)
                {
                    case ConsoleKey.Escape:
                        throw new ClientExit();
                    case ConsoleKey.Enter:
                        return value.ToString();
                    case ConsoleKey.Backspace when value.Length > 0:
                        value.Length--;
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar) && value.Length < 4096)
                        {
                            value.Append(key.KeyChar);
                        }

                        break;
                }
            }

            var text = masked ? new string('*', value.Length) : value.ToString();
            var available = Math.Max(0, terminal.Size.Width - 1);
            if (text.Length > available)
            {
                text = text[^available..];
            }

            terminal.Draw([label, "> " + text, "Enter to continue, Esc to quit"]);
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));

        throw new ClientExit();
    }
}
