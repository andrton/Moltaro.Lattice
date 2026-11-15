using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// An address that names no allocated block of the arena is refused before anything is read
// through it (milestone contract: invalid addresses are rejected before any dereference; brief of
// S04, "Refusals"). IsValid is false for it; Header, Payload and Free refuse it with
// ArgumentException, and the refusal changes nothing. Such an address is the null address, that of
// a freed block, one in a segment the arena does not hold, one inside a block and one beyond the
// end of its segment.
public sealed class InvalidAddressTests
{
    // The payload length of the live block the tests keep next to the address they try.
    private const int LivePayloadLength = 100;

    [Fact(Skip = NotImplemented.Arena)]
    public void The_null_address_is_refused()
    {
        using var arena = Arenas.Open();
        var live = Arenas.Allocate(arena, LivePayloadLength);

        AssertRefused(arena, default, live);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void A_freed_block_is_refused()
    {
        using var arena = Arenas.Open();
        var live = Arenas.Allocate(arena, LivePayloadLength);
        var freed = Arenas.Allocate(arena, LivePayloadLength);

        arena.Free(freed);

        AssertRefused(arena, freed, live);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void A_block_whose_segment_was_freed_is_refused()
    {
        // Without a reserve, the segment that loses its last block is freed at once.
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 4L * Arenas.SegmentSize, ReservedSegments = 0 });
        var freed = Arenas.Allocate(arena, LivePayloadLength);

        arena.Free(freed);

        Assert.Equal(0, arena.Counters.Segments);
        AssertRefused(arena, freed, live: null);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void Freeing_a_block_twice_is_refused_and_changes_nothing()
    {
        using var arena = Arenas.Open();
        var live = Arenas.Allocate(arena, LivePayloadLength);
        var freed = Arenas.Allocate(arena, LivePayloadLength);
        arena.Free(freed);
        var before = arena.Counters;

        Assert.Throws<ArgumentException>(() => arena.Free(freed));

        Assert.Equal(before, arena.Counters);
        Assert.True(arena.IsValid(live));
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void An_address_in_a_segment_the_arena_does_not_hold_is_refused()
    {
        using var arena = Arenas.Open();
        var live = Arenas.Allocate(arena, LivePayloadLength);

        // The arena holds one segment, so no other segment slot is one of its segments.
        Assert.Equal(1, arena.Counters.Segments);

        AssertRefused(arena, new ArenaAddress(live.Segment + 1, live.Offset), live);
        AssertRefused(arena, new ArenaAddress(uint.MaxValue, live.Offset), live);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void An_address_inside_a_block_is_refused()
    {
        using var arena = Arenas.Open();

        // The live block takes a 144-byte slot: every offset from 1 to 143 past its start lies
        // inside it, the first 31 of them inside its header.
        var live = Arenas.Allocate(arena, LivePayloadLength);
        uint[] distances = [1, 8, 16, 31, 32, 33, 64, 143];

        foreach (var distance in distances)
        {
            AssertRefused(arena, new ArenaAddress(live.Segment, live.Offset + distance), live);
        }
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void An_address_beyond_the_end_of_its_segment_is_refused()
    {
        using var arena = Arenas.Open();
        var live = Arenas.Allocate(arena, LivePayloadLength);
        var end = (uint)Arenas.SegmentSize;
        uint[] offsets = [end, end + 32, uint.MaxValue - 15, uint.MaxValue];

        foreach (var offset in offsets)
        {
            AssertRefused(arena, new ArenaAddress(live.Segment, offset), live);
        }
    }

    // The address is not valid, every member that would read through it refuses it, and the
    // refusals change neither the counters nor the live block, when the test keeps one.
    private static void AssertRefused(Arena arena, ArenaAddress address, ArenaAddress? live)
    {
        var before = arena.Counters;

        Assert.False(arena.IsValid(address));
        Assert.Throws<ArgumentException>(() => arena.Header(address));
        Assert.Throws<ArgumentException>(() => arena.Payload(address));
        Assert.Throws<ArgumentException>(() => arena.Free(address));

        Assert.Equal(before, arena.Counters);

        if (live is { } block)
        {
            Assert.True(arena.IsValid(block));
            Assert.Equal((uint)LivePayloadLength, arena.Header(block).PayloadLength);
        }
    }
}
