using Microsoft.AspNetCore.SignalR;

namespace Pongsole.Server;

internal static class GameErrors
{
    internal static HubException InvalidResumeToken() => new("PONGSOLE_INVALID_RESUME_TOKEN");
    internal static HubException ExpiredResumeToken() => new("PONGSOLE_EXPIRED_RESUME_TOKEN");
    internal static HubException SessionAlreadyConnected() => new("PONGSOLE_SESSION_ALREADY_CONNECTED");
    internal static HubException AlreadyJoined() => new("PONGSOLE_ALREADY_JOINED");
    internal static HubException InvalidName() => new("PONGSOLE_INVALID_NAME");
    internal static HubException InvalidDirection() => new("PONGSOLE_INVALID_DIRECTION");
    internal static HubException InvalidSequence() => new("PONGSOLE_INVALID_SEQUENCE");
    internal static HubException NotJoined() => new("PONGSOLE_NOT_JOINED");
    internal static HubException CapacityReached() => new("PONGSOLE_CAPACITY_REACHED");
    internal static HubException InputQueueFull() => new("PONGSOLE_INPUT_QUEUE_FULL");
    internal static HubException ServerStopping() => new("PONGSOLE_SERVER_STOPPING");
}
