namespace Moltaro.Lattice.Storage;

// The settings of one arena, checked by the constructor of Arena. The defaults are the starting
// values of the milestone contract; they are internal tuning, not measured optima.
internal sealed class ArenaOptions
{
    // The most native capacity, in bytes, that the arena may obtain: Segments times SegmentSize,
    // the reserve included. An allocation that would need more fails (Arena.TryAllocatePrepared). The
    // limit is required: nothing means "unlimited", and a limit smaller than one segment, the
    // default 0 among them, is refused.
    public long MemoryLimit { get; init; }

    // The size of one native segment in bytes: the unit in which the arena obtains memory from the
    // native allocator and returns it. At least MaxBlockSize, and a whole multiple of it, because
    // the arena divides a segment into regions of MaxBlockSize bytes; any other size is refused
    // with ArgumentOutOfRangeException. The default, 4 MiB, holds 64 regions; it is the size the
    // contract names as the smallest memory budget of an instance (LatticeDatabaseOptions.MemoryLimit),
    // and both take it from LatticeDatabaseOptions.ArenaSegmentSize.
    public int SegmentSize { get; init; } = LatticeDatabaseOptions.ArenaSegmentSize;

    // How many completely empty segments the arena keeps for reuse instead of freeing them; not
    // negative. They count against MemoryLimit like any other segment.
    public int ReservedSegments { get; init; } = 1;
}
