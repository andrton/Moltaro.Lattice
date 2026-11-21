using Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Limits;

// A failure of the native allocator met inside Complete (brief of S05, "The size of an entity"): it
// is a failed Complete. The OutOfMemoryException passes through, everything the writer allocated is
// freed already, Abandon afterwards does nothing, and every Set… member, SetNull and Complete are
// InvalidOperationException (brief, "The life of a writer"). The root block is the last block a
// writer allocates, so the failure is made to meet its request: a write whose values all lie
// inline asks for nothing before Complete, and a write whose values have blocks finds a region for
// each in the reserve and none for the root block. Each test checks first that no Set… member met
// the failure.
public sealed class CompleteFailureTests
{
    [Fact]
    public void A_native_failure_for_the_root_block_of_inline_values_passes_through_and_ends_the_writer()
    {
        var allocator = new CountingNativeAllocator();
        using var item = new RootArena(EveryType.Compile(), allocator: allocator);
        var writer = item.Writer();

        // Every value that is not nullable, the short text and the three bytes of WriterMembers among
        // them, lies inline: the arena asks the native allocator for nothing before Complete.
        EveryType.SetRequired(ref writer, item);
        allocator.FailNextAllocation = true;

        WriterAssert.Throws<OutOfMemoryException>(ref writer, static (ref RootWriter w) => w.Complete());

        Assert.False(allocator.FailNextAllocation, "The failure was not met in Complete.");
        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
        Assert.Equal(0, allocator.Outstanding);

        foreach (var (_, set, serves) in WriterMembers.Setters)
        {
            WriterAssert.Throws<InvalidOperationException>(ref writer, set(item.EntityType.Fields.First(field => field.Declaration.IsNullable && serves(field.Declaration))));
        }

        WriterAssert.Throws<InvalidOperationException>(ref writer, (ref RootWriter w) => w.SetNull(item.Field("Int32OrNull")));
        WriterAssert.Throws<InvalidOperationException>(ref writer, static (ref RootWriter w) => w.Complete());

        writer.Abandon();
        writer.Abandon();

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
    }

    [Theory]
    [InlineData(1_000, 1)]
    [InlineData(3 * ValueBlocks.ChunkPayload, 4)]
    public void A_native_failure_for_the_root_block_frees_the_value_blocks_written_before_it(int length, int blocks)
    {
        // The reserve holds a segment for the value block, or for the directory and each of the three
        // chunks, and none for the root block. Maybe lies inline, so that the root block is of a size
        // class that no block of the value has.
        using var item = new OneRegionArena("'Type': 'Binary'", reservedSegments: blocks);
        var obtained = item.Allocator.Obtained;
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Value"), Payloads.Bytes(length));
        writer.SetBinary(item.Field("Maybe"), Payloads.Bytes(30));

        Assert.Equal(obtained, item.Allocator.Obtained);
        Assert.Equal(0, item.Arena.Counters.ReservedSegments);
        Assert.NotEqual(StoredBytes.Zero, StoredBytes.In(item.Arena));
        item.Allocator.FailNextAllocation = true;

        WriterAssert.Throws<OutOfMemoryException>(ref writer, static (ref RootWriter w) => w.Complete());

        Assert.False(item.Allocator.FailNextAllocation, "The failure was not met in Complete.");
        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
        item.AssertNativeMemoryIsSegments();

        // With the allocator working again, an open writer would now complete; this one has ended.
        WriterAssert.Throws<InvalidOperationException>(ref writer, static (ref RootWriter w) => w.Complete());

        writer.Abandon();

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
    }
}
