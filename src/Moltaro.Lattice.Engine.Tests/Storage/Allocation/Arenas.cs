using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// Opening an arena and allocating in it, for the tests of the arena. The tests use the default
// segment size and no other: what a segment size has to be beyond the conditions the constructor
// states is the implementer's. Nor do they assume the size of a region or how many regions a
// segment has; where a test needs to know where a block went, it reads that from the counters
// before and after the allocation.
internal static class Arenas
{
    // The default segment size of ArenaOptions, 4 MiB.
    public static int SegmentSize { get; } = new ArenaOptions().SegmentSize;

    // An arena with the default options and a limit of 64 segments, more than any test that opens
    // it allocates. The arena obtains segments only as allocations need them.
    public static Arena Open() => new(new ArenaOptions { MemoryLimit = 64L * SegmentSize });

    // Allocates a block of kind Value with this payload length; the test fails when the arena
    // refuses the allocation for its budget.
    public static ArenaAddress Allocate(Arena arena, int payloadLength)
    {
        var allocated = ArenaBlocks.TryAllocateLive(arena, payloadLength, BlockKindEnum.Value, new RootHandle(1, 1), out var address);

        Assert.True(allocated, "The arena refused an allocation for its budget.");

        return address;
    }

    // Allocates blocks of one size class until its first region is full, and returns the blocks of
    // that region. The arena must hold no block of the class yet. A region is full when the next
    // allocation of the class takes a new region; that block is freed again, so its region, the
    // second of the class, returns to its segment. Afterwards the returned blocks fill the only
    // region of the class, and the class has no free slot.
    public static List<ArenaAddress> FillFirstRegion(Arena arena, int payloadLength)
    {
        var region = new List<ArenaAddress> { Allocate(arena, payloadLength) };

        while (true)
        {
            var before = arena.Counters;
            var block = Allocate(arena, payloadLength);

            if (TookRegion(before, arena.Counters))
            {
                arena.Free(block);

                return region;
            }

            region.Add(block);
        }
    }

    // Allocates blocks of the largest class until the arena holds this many segments, and returns
    // them. The arena must hold no reserved segment, so that a segment is added only when an
    // allocation obtains a new one. The last block returned is then the only block of the last
    // segment.
    public static List<ArenaAddress> AllocateUntilSegments(Arena arena, int segments)
    {
        var blocks = new List<ArenaAddress>();

        while (arena.Counters.Segments < segments)
        {
            blocks.Add(Allocate(arena, SpecifiedSizeClasses.LargestPayload));
        }

        return blocks;
    }

    // Whether an allocation took a region: a free region, a region of a reserved segment or one of
    // a new segment. An allocation that takes a free slot of a region its class already has
    // changes none of these three counters.
    private static bool TookRegion(ArenaCounters before, ArenaCounters after) =>
        (before.FreeRegions, before.ReservedSegments, before.Segments) != (after.FreeRegions, after.ReservedSegments, after.Segments);
}
