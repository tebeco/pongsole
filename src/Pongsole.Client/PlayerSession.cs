using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class PlayerSession
{
    private long lastSent;
    private long lastPulse;
    public Guid? PlayerId { get; private set; }
    public string? Token { get; private set; }
    public Team? Team { get; private set; }

    public void Join(JoinResult result)
    {
        PlayerId = result.PlayerId;
        Token = result.ResumeToken;
        Team = result.Team;
        lastSent = result.LastProcessedSequence;
        lastPulse = lastSent;
    }

    public void Clear()
    {
        lastSent = 0;
        lastPulse = 0;
        PlayerId = null;
        Token = null;
        Team = null;
    }

    public void Reconcile(long lastProcessed) => lastSent = Math.Max(lastSent, lastProcessed);

    public long NextSequence => checked(lastSent + 1);

    public long NextPulse() => lastPulse = Math.Max(NextSequence, checked(lastPulse + 1));
}
