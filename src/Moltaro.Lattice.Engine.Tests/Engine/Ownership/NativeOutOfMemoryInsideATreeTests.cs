using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// A failure of the native allocator met in the middle of a tree leaves nothing behind, at every depth: an
// OutOfMemoryException passes through a List after it has freed what it took, as a value in chunks does
// (Storage.Values.ValueBlocks.TryStore), and the abandon of the writer frees the rest (Storage.Roots.RootWriter,
// NestedValues), so that the arena holds what it held before. The native allocator is asked only for a
// segment, so a request of the allocator of the test fails where the tree needs a new segment
// (CountingNativeAllocator).
// - The smallest case: a List of 819 texts of 64 bytes, a directory of two chunks, into an arena whose first
//   segment has room for the directory and the first chunk only, so that the second chunk needs a new segment.
// - Every segment a tree needs, in turn, for trees whose segments are first needed by different blocks: the
//   chunks of a List of records, the chunks of a packed List, the blocks of values inside records, a packed
//   List inside a record, a List of records inside a record, and trees of TreeSchema.
// - Through the database: a write whose List of records meets the failure ends in the exception, the
//   instance stays Ready, and after a drain the arena holds the live and prepared bytes it held before and
//   the ownership audit finds every block owned by exactly one live entity (OwnershipAudit).
public sealed class NativeOutOfMemoryInsideATreeTests
{
    private const int SegmentSize = 4 * 1024 * 1024;

    public static TheoryData<string> Trees => new() { "records", "packed", "values in records", "packed in records", "records in records", "tree shape 2", "tree shape 4" };

    [Fact]
    public void Native_out_of_memory_at_the_second_chunk_of_a_List_of_records_leaves_nothing_allocated()
    {
        var allocator = new CountingNativeAllocator();
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 64L * SegmentSize, ReservedSegments = 0 }, allocator, new InstanceMemoryBudget());

        // The 64 regions of the first segment, all but two.
        for (var region = 0; region < 62; region++)
        {
            Assert.True(arena.TryAllocatePrepared(Arena.MaxBlockSize - BlockHeader.Size, BlockKindEnum.Value, new RootHandle(9, 9), out _));
        }

        var layout = RootLayout.Compile(Item, TreeSchema.Schema);

        Assert.True(Item.TryGetField("Tags", out var tags));

        var before = arena.Counters;
        var writer = new RootWriter(arena, layout, new RootHandle(1, 1));
        var thrown = false;

        allocator.FailAt = allocator.Requests + 1;

        try
        {
            writer.SetList(tags, ManyTexts(819).ToList());
        }
        catch (OutOfMemoryException)
        {
            thrown = true;
        }

        writer.Abandon();

        Assert.True(thrown && allocator.Failed);
        Assert.Equal(before, arena.Counters);
    }

    [Theory]
    [MemberData(nameof(Trees))]
    public async Task Native_out_of_memory_at_every_segment_a_tree_needs_leaves_the_arena_as_it_was(string tree)
    {
        object?[] values;

        await using (var database = await EngineMaster.OpenAsync(TreeSchema.Schema))
        {
            values = ValuesOf(database.Set("Item"), tree);
        }

        var failures = new List<string>();

        for (var failAt = 1; ; failAt++)
        {
            var allocator = new CountingNativeAllocator { FailAt = failAt };
            using var arena = new Arena(new ArenaOptions { MemoryLimit = 64L * SegmentSize, ReservedSegments = 0 }, allocator, new InstanceMemoryBudget());
            var completed = TryWrite(arena, values);
            var counters = arena.Counters;
            var held = (counters.LiveBytes, counters.ClassSlackBytes, counters.PreparedBytes, counters.RetiredBytes, arena.Texts.Count);

            if (held != (0L, 0L, 0L, 0L, 0))
            {
                failures.Add($"request {failAt} failed: the arena holds {held}");
            }

            if (completed)
            {
                Assert.False(allocator.Failed);

                break;
            }
        }

        Assert.True(failures.Count == 0, $"{tree}: {string.Join("; ", failures)}");
    }

    [Fact]
    public async Task Native_out_of_memory_while_the_chunks_of_a_List_of_records_are_allocated_leaves_nothing_behind()
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await EngineMaster.OpenAsync(WideTreeSchema.Schema, new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize }, allocator);
        var items = database.Set("Item");

        await items.InsertAsync(WideTreeSchema.ItemOf(items, 1, ["small"]));
        database.DrainRetired();

        var before = (database.Counters.Arena.LiveBytes, database.Counters.Arena.PreparedBytes);
        var tags = ManyTexts(818 * 100);

        allocator.FailNextAllocation = true;

        await Assert.ThrowsAsync<OutOfMemoryException>(() => items.InsertAsync(WideTreeSchema.ItemOf(items, 2, tags)).AsTask().WaitAsync(Bound));

        database.DrainRetired();

        Assert.Equal(before, (database.Counters.Arena.LiveBytes, database.Counters.Arena.PreparedBytes));
        OwnershipAudit.Expect(database);
    }

    [Fact]
    public async Task Write_of_a_List_of_records_that_meets_native_out_of_memory_leaves_the_instance_Ready_and_every_block_owned()
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await EngineMaster.OpenAsync(TreeSchema.Schema, new ArenaOptions { MemoryLimit = 64L * SegmentSize }, allocator);
        var items = database.Set("Item");

        await items.InsertAsync(TreeSchema.RecordOf(items, 1, shape: 1, number: 1));
        OwnershipAudit.Expect(database);

        var record = TreeSchema.RecordOf(items, 2, shape: 0, number: 2);

        record["Tags"] = ManyTexts(70 * 818);
        allocator.FailNextAllocation = true;

        await Assert.ThrowsAsync<OutOfMemoryException>(() => items.UpsertAsync(record).AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        OwnershipAudit.Expect(database);
    }

    private static LatticeEntityType Item => TreeSchema.Schema.TryGetEntityType("Item", out var item) ? item : throw new InvalidOperationException();

    // Writes the values into a root of the arena and frees it, true; or meets the failure of the allocator and
    // abandons the writer, as the engine does, false.
    private static bool TryWrite(Arena arena, object?[] values)
    {
        var layout = RootLayout.Compile(Item, TreeSchema.Schema);
        var writer = new RootWriter(arena, layout, new RootHandle(1, 1));

        try
        {
            RootValues.Write(ref writer, Item, values);
            RootStorage.Free(arena, layout, writer.Complete());

            return true;
        }
        catch (OutOfMemoryException)
        {
            writer.Abandon();

            return false;
        }
    }

    // The values of Item for the tree, by the ordinals of its fields, in the form a record holds them.
    private static object?[] ValuesOf(ILatticeSet items, string tree)
    {
        LatticeRecord record;

        switch (tree)
        {
            case "records":
                // 70 chunks of 818 texts of 64 bytes: more than the 64 regions of one segment.
                record = Bare();
                record["Tags"] = ManyTexts(70 * 818);
                break;

            case "packed":
                // 1,200,000 Int64, about 9.6 MB in chunks.
                record = Bare();
                record["Scores"] = Enumerable.Range(0, 1_200_000).Select(index => (long)index).ToArray();
                break;

            case "values in records":
                // Files of 3,000 Binary values of 3,000 bytes, each in a block of its own inside the records.
                record = Bare();
                record["Files"] = Enumerable.Range(0, 3_000).Select(_ => new byte[3_000]).ToArray();
                break;

            case "packed in records":
                // Grid of 12 Lists of 300,000 Int32 each, a packed List in chunks inside each record.
                record = Bare();
                record["Grid"] = Enumerable.Range(0, 12).Select(index => Enumerable.Repeat(index, 300_000).ToList()).ToArray();
                break;

            case "records in records":
                // Places of 100 Addresses each with 820 lines of 64 bytes, a List of records in two chunks
                // inside each record.
                Assert.True(TreeSchema.Schema.TryGetOwnedType("Address", out var address));
                record = Bare();
                record["Places"] = Enumerable.Range(0, 100).Select(index => new LatticeOwnedValue(address) { ["City"] = $"c{index}", ["Lines"] = ManyTexts(820) }).ToArray();
                break;

            case "tree shape 2":
                record = TreeSchema.RecordOf(items, 1, shape: 2, number: 1);
                break;

            default:
                record = TreeSchema.RecordOf(items, 1, shape: 4, number: 1);
                break;
        }

        return [.. Item.Fields.Select(field => record[field.Name])];
    }

    private static LatticeRecord Bare() => new(Item)
    {
        ["Id"] = 1L,
        ["Name"] = "name",
        ["Tags"] = Array.Empty<string>(),
        ["Scores"] = Array.Empty<long>(),
        ["Places"] = Array.Empty<LatticeOwnedValue>(),
        ["Grid"] = Array.Empty<List<int>>(),
        ["Files"] = Array.Empty<byte[]>(),
    };

    private static string[] ManyTexts(int count) => [.. Enumerable.Range(0, count).Select(index => index.ToString("D64", CultureInfo.InvariantCulture))];
}
