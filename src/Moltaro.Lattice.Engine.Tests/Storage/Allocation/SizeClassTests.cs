using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// The size classes of blocks (milestone contract, "Native layout and allocation"; Q17, "Size
// classes and reusable slots"). Arena.BlockSizeFor gives the smallest class that holds the 32-byte
// header and the payload: 16-byte steps through 1 KiB, 256-byte steps through 4 KiB, then powers of
// two through MaxBlockSize, 64 KiB. A payload of 0 bytes takes the smallest class, 32 bytes; the
// largest payload is MaxBlockSize - 32 = 65,504 bytes. A negative length, or one that does not fit
// MaxBlockSize with the header, is refused with ArgumentOutOfRangeException by BlockSizeFor and by
// TryAllocate alike.
public sealed class SizeClassTests
{
    public static TheoryData<int> EverySizeClass => new(SpecifiedSizeClasses.All);

    [Fact(Skip = NotImplemented.Arena)]
    public void Every_payload_length_gets_the_smallest_size_class_that_holds_it_with_its_header()
    {
        for (var payloadLength = 0; payloadLength <= SpecifiedSizeClasses.LargestPayload; payloadLength++)
        {
            Assert.Equal(SpecifiedSizeClasses.For(payloadLength), Arena.BlockSizeFor(payloadLength));
        }
    }

    [Theory(Skip = NotImplemented.Arena)]
    [InlineData(0, 32)]
    [InlineData(1, 48)]
    [InlineData(16, 48)]
    [InlineData(17, 64)]
    [InlineData(992, 1024)]
    [InlineData(993, 1280)]
    [InlineData(1248, 1280)]
    [InlineData(1249, 1536)]
    [InlineData(4064, 4096)]
    [InlineData(4065, 8192)]
    [InlineData(8160, 8192)]
    [InlineData(8161, 16384)]
    [InlineData(16352, 16384)]
    [InlineData(16353, 32768)]
    [InlineData(32736, 32768)]
    [InlineData(32737, 65536)]
    [InlineData(65504, 65536)]
    public void A_payload_at_a_boundary_of_the_steps_gets_the_class_the_specification_gives(int payloadLength, int blockSize)
    {
        Assert.Equal(blockSize, Arena.BlockSizeFor(payloadLength));
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void A_payload_of_no_bytes_takes_the_smallest_class_the_header_alone()
    {
        using var arena = Arenas.Open();

        Assert.Equal(32, Arena.BlockSizeFor(0));

        var address = Arenas.Allocate(arena, 0);

        Assert.True(arena.IsValid(address));
        Assert.Equal(0u, arena.Header(address).PayloadLength);
        Assert.True(arena.Payload(address).IsEmpty);
        Assert.Equal(32, arena.Counters.LiveBytes);
        Assert.Equal(0, arena.Counters.ClassSlackBytes);
    }

    [Fact(Skip = NotImplemented.Arena)]
    public void The_largest_payload_is_65504_bytes_in_a_block_of_MaxBlockSize()
    {
        using var arena = Arenas.Open();

        Assert.Equal(64 * 1024, Arena.MaxBlockSize);
        Assert.Equal(Arena.MaxBlockSize, Arena.BlockSizeFor(65_504));

        var address = Arenas.Allocate(arena, 65_504);

        Assert.Equal(65_504u, arena.Header(address).PayloadLength);
        Assert.Equal(65_504, arena.Payload(address).Length);
        Assert.Equal(Arena.MaxBlockSize, arena.Counters.LiveBytes);
        Assert.Equal(0, arena.Counters.ClassSlackBytes);
    }

    [Theory(Skip = NotImplemented.Arena)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(65_505)]
    [InlineData(int.MaxValue)]
    public void A_length_that_is_negative_or_does_not_fit_the_largest_block_is_refused(int payloadLength)
    {
        using var arena = Arenas.Open();
        Arenas.Allocate(arena, 100);
        var before = arena.Counters;

        Assert.Throws<ArgumentOutOfRangeException>(() => Arena.BlockSizeFor(payloadLength));
        Assert.Throws<ArgumentOutOfRangeException>(() => arena.TryAllocate(payloadLength, BlockKindEnum.Value, new RootHandle(1, 1), out _));
        Assert.Equal(before, arena.Counters);
    }

    [Theory(Skip = NotImplemented.Arena)]
    [MemberData(nameof(EverySizeClass))]
    public void A_block_of_every_size_class_is_allocated_freed_and_its_slot_reused(int blockSize)
    {
        using var arena = Arenas.Open();
        var payloadLength = blockSize - SpecifiedSizeClasses.HeaderSize;

        // The region is the only one of its class and has no free slot.
        var region = Arenas.FillFirstRegion(arena, payloadLength);
        var freed = region[^1];
        Assert.All(region, block => Assert.True(arena.IsValid(block)));
        var before = arena.Counters;

        arena.Free(freed);

        Assert.False(arena.IsValid(freed));

        var again = Arenas.Allocate(arena, payloadLength);

        // The block took the space the freed one gave back, and no other.
        Assert.True(arena.IsValid(again));
        Assert.Equal(before, arena.Counters);

        // While the region keeps other blocks, the freed slot is the only free slot of the class,
        // and the next block of the class takes it. A region that held one block of the class
        // returned to its segment when that block was freed; it has no slot left to name.
        if (region.Count > 1)
        {
            Assert.Equal(freed, again);
        }
    }
}
