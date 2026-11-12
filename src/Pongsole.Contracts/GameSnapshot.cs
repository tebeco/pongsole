using System.Collections.Immutable;

namespace Pongsole.Contracts;

public sealed record GameSnapshot(ArenaState Arena, ImmutableArray<PlayerState> Players);
