namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal readonly record struct AllocationInfo(
    BlockAddress Address, BlockOwner Owner, int Length, int Capacity, BlockStateEnum State);
