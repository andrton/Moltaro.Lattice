namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal readonly record struct BlockOwner(BlockKindEnum Kind, int Entry, int Generation, int Ordinal = 0);
