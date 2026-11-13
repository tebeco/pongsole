using Pongsole.Contracts;

namespace Pongsole.Core;

public readonly record struct PaddleParticipant(Guid PlayerId, Team Team);
