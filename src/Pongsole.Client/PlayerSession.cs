using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class PlayerSession
{
    private long lastSent;
    public Guid? PlayerId { get; private set; }
    public string? Token { get; private set; }
    public Team? Team { get; private set; }

    public void Join(JoinResult result)
    {
        if (PlayerId != result.PlayerId)
        {
            lastSent = 0;
        }

        PlayerId = result.PlayerId;
        Token = result.ResumeToken;
        Team = result.Team;
        lastSent = Math.Max(lastSent, result.LastProcessedSequence);
    }

    public void Clear()
    {
        lastSent = 0;
        PlayerId = null;
        Token = null;
        Team = null;
    }

    public void Reconcile(long lastProcessed) => lastSent = Math.Max(lastSent, lastProcessed);

    public long NextSequence(long lastProcessed) => lastSent = checked(Math.Max(lastSent, lastProcessed) + 1);
}
