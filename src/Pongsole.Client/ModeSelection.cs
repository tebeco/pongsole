using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class ModeSelection(IGameConnection connection, CancellationToken cancellationToken)
{
    private Task? pending;
    private string? message;

    public string? Message => pending is not null ? "Selecting mode…" : message;

    public async Task ObserveAsync(bool awaiting)
    {
        if (!awaiting) message = null;
        if (pending is not { IsCompleted: true }) return;
        try
        {
            await pending;
            if (awaiting) message = "Mode selected. Waiting for arena update.";
        }
        catch (GameProtocolException error)
        {
            if (awaiting) message = ClientError.Marker(error) switch
            {
                "PONGSOLE_MODE_FORBIDDEN" => "The selector changed. Waiting for the current selector.",
                "PONGSOLE_MODE_LOCKED" => "Mode already selected. Waiting for arena update.",
                _ => "Mode selection failed. Try again."
            };
        }
        finally { pending = null; }
    }

    public void Select(ConsoleKey key)
    {
        if (pending is not null || message is "Mode selected. Waiting for arena update.") return;
        var mode = key switch
        {
            ConsoleKey.D1 or ConsoleKey.NumPad1 => GameMode.AllTogether,
            ConsoleKey.D2 or ConsoleKey.NumPad2 => GameMode.IndividualPaddles,
            _ => GameMode.AwaitingSelection
        };
        if (mode != GameMode.AwaitingSelection)
            pending = connection.SelectModeAsync(mode, cancellationToken);
    }

    public async Task FinishAsync()
    {
        if (pending is not null)
        {
            try { await pending; }
            catch (Exception) { }
        }
    }
}
