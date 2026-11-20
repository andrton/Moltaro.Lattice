using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// A length in a header is read from memory that a caller can write, and the milestone contract
// asks that an invalid length be rejected before it is used. The arena keeps no length of its own
// (brief of S04, "Counters"), so Free takes the length for LiveBytes from the header. A length
// beyond the capacity of the slot is refused by Free with InvalidOperationException, as Payload
// refuses it, and the block stays allocated with the counters as they were. A length within the
// slot that differs from the one given would shift bytes between LiveBytes and ClassSlackBytes;
// a caller does not write the length through the header, and no test here does that.
public sealed class DamagedHeaderTests
{
    [Fact]
    public void Free_refuses_a_header_length_beyond_the_slot_and_leaves_the_block_and_the_counters_as_they_were()
    {
        using var arena = Arenas.Open();
        var address = Arenas.Allocate(arena, 100);
        var before = arena.Counters;
        var room = SpecifiedSizeClasses.For(100) - SpecifiedSizeClasses.HeaderSize;
        uint[] lengths = [(uint)room + 1, uint.MaxValue];

        foreach (var length in lengths)
        {
            arena.Header(address).PayloadLength = length;

            Assert.Throws<InvalidOperationException>(() => arena.Free(address));

            Assert.True(ArenaBlocks.IsValid(arena, address));
            Assert.Equal(before, arena.Counters);
        }

        // With its length given back, the block is freed and leaves the counters.
        arena.Header(address).PayloadLength = 100;
        arena.Free(address);

        Assert.False(ArenaBlocks.IsValid(arena, address));
        Assert.Equal(0, arena.Counters.LiveBytes);
        Assert.Equal(0, arena.Counters.ClassSlackBytes);
    }
}
