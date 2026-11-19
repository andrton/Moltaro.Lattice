namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

// The state a block is freed from, so that its slot leaves the right count (NodeArena.Free).
internal enum BlockStateEnum
{
    Live,
    Prepared,
    Retired,
}
