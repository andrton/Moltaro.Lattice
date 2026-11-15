namespace Moltaro.Lattice.Storage;

// The settings of one arena. The starting values are those of the milestone contract ("Initial
// bounded settings"); they are internal tuning, not measured optima.
internal sealed class ArenaOptions
{
    // The most native segment capacity, in bytes, that the arena may hold, the reserve of empty
    // segments included. An allocation that would need more fails (Arena.TryAllocate). It has no
    // default of its own: whoever creates the arena sets it from the memory budget of the instance.
    public long MemoryLimit { get; init; }

    // The size of one native segment in bytes: the unit in which the arena obtains memory from the
    // native allocator and returns it.
    public int SegmentSize { get; init; } = 4 * 1024 * 1024;

    // How many completely empty segments the arena keeps for reuse instead of freeing them. They
    // count against MemoryLimit like any other segment.
    public int ReservedSegments { get; init; } = 1;
}
