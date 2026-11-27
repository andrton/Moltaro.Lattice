using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Primary;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// A directory that grows by a larger copy may still be held by a read that has not seen the copy: the
// table of segments of the arena, the directory of pages of the table of root entries and the directory
// of the overflow pages of a primary index. The old one is a generation a reader may still hold, so it
// stays charged, and reported among the retired generations, until that read has left and a look has
// run; then the charge is lower by exactly that array (docs/design/storage-and-maintenance.md, the
// memory budget). Each case grows one directory once, and nothing else, while a read is held.
public sealed class ReplacedDirectoryTests
{
    private const long Limit = 64L * Segment;

    [Fact]
    public async Task The_segment_table_a_read_may_hold_stays_charged_until_the_read_leaves()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var pieces = node.Set<PieceSchema.Piece>();

        // A piece of this size fills a segment of its own; the table of segments has four places, the
        // first unused, so the fourth piece doubles it.
        var data = Segment - 4 * Arena.MaxBlockSize;
        for (var id = 1L; id <= 3; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, 8, data: data)).AsTask().WaitAsync(Bound);
        }

        await ExpectHeldUntilTheReadLeavesAsync(node, allocator, new HeldArray("Arena", typeof(ArenaSegment), 4), growths: 1,
            () => pieces.InsertAsync(PieceSchema.Of(4, 8, data: data)).AsTask());
    }

    [Fact]
    public async Task Two_growths_of_the_segment_table_under_one_read_keep_both_old_tables_charged_until_the_read_leaves()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var pieces = node.Set<PieceSchema.Piece>();

        // The fourth piece doubles the table of four places, the eighth the table of eight.
        var data = Segment - 4 * Arena.MaxBlockSize;
        for (var id = 1L; id <= 3; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, 8, data: data)).AsTask().WaitAsync(Bound);
        }

        await ExpectHeldUntilTheReadLeavesAsync(node, allocator, new HeldArray("Arena", typeof(ArenaSegment), 4), growths: 2, async () =>
        {
            for (var id = 4L; id <= 8; id++)
            {
                await pieces.InsertAsync(PieceSchema.Of(id, 8, data: data)).AsTask();
            }
        });
    }

    [Fact]
    public async Task The_directory_of_root_pages_a_read_may_hold_stays_charged_until_the_read_leaves()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var holders = node.Set("Holder");

        // The first page of root entries holds RootTable.EntriesPerPage roots. Its last places go to the
        // entity type Holder, whose small index does not grow at the root that adds the second page, as
        // the index of the pieces would.
        const int Holders = 7;
        const int Pieces = RootTable.EntriesPerPage - Holders + 1;
        for (var first = 1L; first <= Pieces; first += 512)
        {
            var batch = node.CreateWriteBatch();
            for (var id = first; id < first + 512 && id <= Pieces; id++)
            {
                batch.Insert(PieceSchema.Of(id, 8));
            }

            await batch.CommitAsync().AsTask().WaitAsync(Bound);
        }

        for (var id = 1L; id < Holders; id++)
        {
            await holders.InsertAsync(Holder(holders, id)).AsTask().WaitAsync(Bound);
        }

        await ExpectHeldUntilTheReadLeavesAsync(node, allocator, new HeldArray("RootTable", typeof(RootEntry[]), 1), growths: 1,
            () => holders.InsertAsync(Holder(holders, Holders)).AsTask());
    }

    [Fact]
    public async Task The_directory_of_overflow_pages_a_read_may_hold_stays_charged_until_the_read_leaves()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        var pieces = node.Set<PieceSchema.Piece>();

        // Keys of one hash lengthen one chain of overflow buckets: the fifteenth takes a second overflow
        // page, which doubles the directory of one page, while the main table keeps its size.
        var keys = CollidingKeys.Of(15);
        foreach (var key in keys[..14])
        {
            await pieces.InsertAsync(PieceSchema.Of(key, 8)).AsTask().WaitAsync(Bound);
        }

        await ExpectHeldUntilTheReadLeavesAsync(node, allocator, new HeldArray("PrimaryIndex", typeof(OverflowPage), 1), growths: 1,
            () => pieces.InsertAsync(PieceSchema.Of(keys[14], 8)).AsTask(), keys[0]);
    }

    // A read is held across the growths of the directory: with it held the charge is the inventory with
    // each old directory, which the retirement reports; after the read has left and a look has run, the
    // charge is lower by exactly the old directories, which are no longer reported.
    private static async Task ExpectHeldUntilTheReadLeavesAsync(EngineDatabase node, CountingNativeAllocator allocator, HeldArray directory,
        int growths, Func<Task> grow, long readKey = 1)
    {
        var pieces = node.Set<PieceSchema.Piece>();
        node.DrainRetired();
        var before = ExpectAtRest(node, allocator);
        Assert.Contains(directory, before.Arrays);

        var replaced = Enumerable.Range(0, growths).Select(growth => directory with { Owner = "RetiredMemory", Length = directory.Length << growth }).ToList();
        var grown = directory with { Length = directory.Length << growths };
        using var reader = await HeldRead.StartAsync(node, () => pieces.Get(readKey)?.Id);
        await grow().WaitAsync(Bound);

        var held = ExpectAtRest(node, allocator);
        Assert.Contains(grown, held.Arrays);
        Assert.All(replaced, array => Assert.Contains(array, held.Arrays));

        Assert.Equal(readKey, await reader.ReleaseAsync());
        node.DrainRetired();
        var after = ExpectAtRest(node, allocator);
        Assert.All(replaced, array => Assert.DoesNotContain(array, after.Arrays));
        Assert.Equal(held.Charged - replaced.Sum(ArrayBytes), after.Charged);
    }

    private static LatticeRecord Holder(ILatticeSet holders, long id) =>
        new(holders.EntityType) { ["Id"] = id, ["Lines"] = new Dictionary<string, string>() };
}
