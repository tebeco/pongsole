using System.Threading.Channels;
using Pongsole.Contracts;

namespace Pongsole.Server;

public interface IGameCoordinator
{
    Task<JoinResult> JoinAsync(string connectionId, string displayName, string? resumeToken);

    Task PushAsync(string connectionId, Direction direction, long sequence);

    Task DisconnectAsync(string connectionId);

    ChannelReader<GameSnapshot> Subscribe(string connectionId);
}
