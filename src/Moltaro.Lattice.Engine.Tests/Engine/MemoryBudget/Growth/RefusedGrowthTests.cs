using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.Storage.Hashing;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Growth;

// A refused preparation returns what it reserved and every block, slot and text it prepared, and
// none of its data becomes visible; the capacity that the text table and a primary table grew for it
// stays with them, charged and reusable. The same write afterwards needs no growth and takes no new
// charge for one. A preparation places every root before it reserves the room of the primary indexes
// in the order of its members, so the refusal comes from the full table of Counted, whose room the
// last member needs after the table of Uncounted grew. A refused write is resolved by freeing what is
// retired and trying again: Counted holds so many entries that its growth needs more than the hold
// leaves and everything that resolution can free, the old table of Uncounted and every array of the
// text table, so the second try is refused too.
public sealed class RefusedGrowthTests
{
    private const long Limit = 64L * Segment;
    private const int Texts = 128;

    // The table of Counted, of 2^CountedBits buckets, is full with this many entries, so that its next
    // insert grows it to 2^(CountedBits + 1) buckets; Uncounted and Ranked hold eight.
    private const int CountedBits = 7;
    private const int CountedRows = IndexTable.MostEntriesPerBucket << CountedBits;
    private const int Rows = 8;

    [Fact]
    public async Task A_write_needing_the_growth_a_refused_write_made_takes_no_new_charge_and_the_refused_data_stays_invisible()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator, schema: IndexSchema.Utf8);
        var ranked = node.Set<IndexSchema.Ranked>();
        var uncounted = node.Set<IndexSchema.Uncounted>();
        var counted = node.Set<IndexSchema.Counted>();
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Tables.Stored(3).Presize(1_024);
            return true;
        });
        for (var key = 1; key <= Rows; key++)
        {
            await ranked.InsertAsync(Rank(key, $"region {key}"));
            await uncounted.InsertAsync(new IndexSchema.Uncounted { Id = key, Number = key });
            await counted.InsertAsync(new IndexSchema.Counted { Id = key, Number = key });
        }

        // The rest of the entries of Counted, under keys and numbers no write of the test uses.
        var more = node.CreateWriteBatch();
        for (var key = 1_000; key < 1_000 + CountedRows - Rows; key++)
        {
            more.Insert(new IndexSchema.Counted { Id = key, Number = key });
        }

        await more.CommitAsync().AsTask().WaitAsync(Bound);
        node.DrainRetired();
        var initial = node.Position;
        var references = node.TextReferences().Count;
        var before = ExpectAtRest(node, allocator);

        IDisposable? held = null;
        var countedGrew = false;
        try
        {
            node.ArmGrowthSwitch(uncounted.EntityType, () => held = LeaveAbout(node, 32, out _));
            node.ArmGrowthSwitch(counted.EntityType, () => countedGrew = true);
            var refused = Batch(node);
            refused.Insert(new IndexSchema.Counted { Id = 9, Number = 9 });
            await RefusedAsync(refused.CommitAsync().AsTask(), LatticeLimitEnum.Memory);
            Assert.NotNull(held);
            Assert.False(countedGrew);
        }
        finally
        {
            held?.Dispose();
        }

        Assert.Equal(initial, node.Position);
        Assert.Null(uncounted.Get(9L));
        Assert.Null(ranked.Get(9L));
        Assert.Empty(ranked.Index("ByNumber").Find(IndexKey.Equal(9L)));
        Assert.Empty(ranked.Index("ByRegion").Find(IndexKey.Equal(NewRegion(9))));
        Assert.Equal(8, ranked.Enumerate().Count());
        Assert.Equal(8, uncounted.Enumerate().Count());
        Assert.Null(counted.Get(9L));
        Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(9L)));
        Assert.Equal(CountedRows, counted.Enumerate().Count());
        Assert.Equal(references, node.TextReferences().Count);
        Assert.Equal(2 * Rows + CountedRows, node.Counters.RootEntries);
        OwnershipAudit.Expect(node);
        var rest = ExpectAtRest(node, allocator);
        Assert.NotEmpty(Added(rest.Arrays, before.Arrays));

        // What the sizes are for: the growth of Counted needs more than the hold left, at most 39 bytes,
        // the old table of Uncounted and every array of the text table, all of which the resolution of
        // the refusal could free.
        var textArrays = rest.Arrays.Where(array => array.Owner == "TextTable").Sum(ArrayBytes);
        Assert.True(TableBytes(CountedBits + 1) > 39 + TableBytes(1) + textArrays);

        var grew = false;
        node.ArmGrowthSwitch(uncounted.EntityType, () => grew = true);
        var written = await Batch(node).CommitAsync().AsTask().WaitAsync(Bound);
        Assert.False(grew);
        Assert.Equal(written.Position, node.Position);
        Assert.Equal(9L, uncounted.Get(9L)?.Id);
        Assert.Equal(NewRegion(9), Assert.Single(ranked.Index("ByRegion").Find(IndexKey.Equal(NewRegion(9)))).Region);
        var after = ExpectAtRest(node, allocator);
        Assert.Empty(Added(after.Arrays, rest.Arrays));
        Assert.Equal(rest.Charged, after.Charged);
    }

    // New Dictionary texts first, which grow the text table, then the insert that grows the full
    // primary table of Uncounted.
    private static ILatticeWriteBatch Batch(EngineDatabase node)
    {
        var batch = node.CreateWriteBatch();
        for (var key = 9; key < 9 + Texts; key++)
        {
            batch.Insert(Rank(key, NewRegion(key)));
        }

        batch.Insert(new IndexSchema.Uncounted { Id = 9, Number = 9 });
        return batch;
    }

    // The arrays of a table of 2^bucketBits buckets (IndexTable): a main array of eight words a bucket and
    // seven words of alignment, and one overflow page of a sixteenth of the buckets, at least one, in a
    // directory of one page.
    private static long TableBytes(int bucketBits)
    {
        var mainWords = (IndexTable.WordsPerBucket << bucketBits) + 7;
        var pageWords = (IndexTable.WordsPerBucket << Math.Max(0, bucketBits - 4)) + 7;
        return ArrayBytes(typeof(long), mainWords) + ArrayBytes(typeof(OverflowPage), 1) + ArrayBytes(typeof(long), pageWords);
    }

    private static IndexSchema.Ranked Rank(int key, string region) =>
        new() { Id = key, Number = key, Login = $"login {key}", Region = region };

    private static string NewRegion(int key) => $"new region {key}";
}
