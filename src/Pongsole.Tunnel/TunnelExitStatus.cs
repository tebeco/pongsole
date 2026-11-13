namespace Pongsole.Tunnel;

internal sealed class TunnelExitStatus
{
    private int code;

    internal int Code => Volatile.Read(ref code);

    internal void MarkFailed() => Volatile.Write(ref code, 1);
}
