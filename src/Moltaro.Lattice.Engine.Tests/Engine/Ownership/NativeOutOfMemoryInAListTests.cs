using System.Globalization;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// A write whose List of records meets a failure of the native allocator while the chunks of its records are
// allocated leaves nothing behind, as a value in chunks does (Storage.Values.ValueBlocks.TryStore): the
// directory and the chunks taken so far are freed before the OutOfMemoryException passes on, so that after a
// drain the arena holds the bytes it held before, live and prepared, and the ownership audit finds every block
// owned by exactly one live entity. The List is Tags of WideTreeSchema: 100 chunks of 818 records, about 6.5 MB,
// more than one segment holds, so that the arena asks the allocator of the test for a new segment in the
// middle of the chunks, and that request fails (CountingNativeAllocator).
public sealed class NativeOutOfMemoryInAListTests
{
    [Fact]
    public async Task Native_out_of_memory_while_the_chunks_of_a_List_of_records_are_allocated_leaves_nothing_behind()
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await EngineMaster.OpenAsync(WideTreeSchema.Schema, new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize }, allocator);
        var items = database.Set("Item");

        await items.InsertAsync(WideTreeSchema.ItemOf(items, 1, ["small"]));
        database.DrainRetired();

        var before = (database.Counters.Arena.LiveBytes, database.Counters.Arena.PreparedBytes);
        var tags = Enumerable.Range(0, 818 * 100).Select(index => index.ToString("D64", CultureInfo.InvariantCulture)).ToArray();

        allocator.FailNextAllocation = true;

        await Assert.ThrowsAsync<OutOfMemoryException>(() => items.InsertAsync(WideTreeSchema.ItemOf(items, 2, tags)).AsTask().WaitAsync(Bound));

        database.DrainRetired();

        Assert.Equal(before, (database.Counters.Arena.LiveBytes, database.Counters.Arena.PreparedBytes));
        OwnershipAudit.Expect(database);
    }
}
