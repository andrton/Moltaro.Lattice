namespace Moltaro.Lattice.Storage;

// The settings of one arena, checked by the constructor of Arena. The defaults are the starting
// values of the milestone contract; they are internal tuning, not measured optima.
internal sealed class ArenaOptions
{
    // The most native capacity, in bytes, that the arena may obtain: Segments times SegmentSize,
    // the reserve included. An allocation that would need more fails (Arena.TryAllocate). The
    // limit is required: nothing means "unlimited", and a limit smaller than one segment, the
    // default 0 among them, is refused.
    public long MemoryLimit { get; init; }

    // The size of one native segment in bytes: the unit in which the arena obtains memory from the
    // native allocator and returns it. At least MaxBlockSize.
    public int SegmentSize { get; init; } = 4 * 1024 * 1024;

    // How many completely empty segments the arena keeps for reuse instead of freeing them; not
    // negative. They count against MemoryLimit like any other segment.
    public int ReservedSegments { get; init; } = 1;
}
