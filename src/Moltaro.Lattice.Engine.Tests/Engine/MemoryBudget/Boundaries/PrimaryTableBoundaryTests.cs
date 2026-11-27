using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Hashing;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Boundaries;

// A primary index grows its table by allocating the table of twice the buckets while the old one
// stays charged for the readers that may hold it. The insert that grows it is admitted exactly when
// the limit holds what the instance holds, the estimate of the write and the arrays of the new
// table, derived from the layout of IndexTable: a main array of eight words a bucket and seven words
// of alignment, and an overflow page of a sixteenth of the buckets, at least one, in a directory of
// one page.
public sealed class PrimaryTableBoundaryTests
{
    private const int Inserts = 16;

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task The_insert_that_grows_a_primary_table_is_admitted_exactly_when_both_generations_fit(int difference)
    {
        long before;
        long required;
        int bucketBits;
        var witness = new CountingNativeAllocator();
        await using (var control = await OpenAsync(32L * Segment, witness))
        {
            Assert.Equal(Inserts / 2 + 1, await FillAsync(control));
            // Each table holds four entries a bucket: the table that the last insert outgrows has
            // a quarter of its entries in buckets.
            bucketBits = int.Log2(Inserts / IndexTable.MostEntriesPerBucket);
            before = ExpectAtRest(control, witness).Charged;
            control.ResetBudgetPeak();
            var grows = false;
            control.ArmGrowthSwitch(control.Set<PieceSchema.Piece>().EntityType, () => grows = true);
            await control.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(Inserts + 1, 8));
            Assert.True(grows);
            var after = ExpectAtRest(control, witness);
            Assert.Equal(TableBytes(bucketBits + 1), after.Charged - before);
            required = before + UnitEstimate(1) + TableBytes(bucketBits + 1);
            Assert.Equal(required, after.Peak);
        }

        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(required + difference, allocator);
        _ = await FillAsync(node);
        Assert.Equal(before, ExpectAtRest(node, allocator).Charged);
        var pieces = node.Set<PieceSchema.Piece>();
        var grew = false;
        node.ArmGrowthSwitch(pieces.EntityType, () => grew = true);
        var insert = pieces.InsertAsync(PieceSchema.Of(Inserts + 1, 8)).AsTask();
        if (difference < 0)
        {
            await RefusedAsync(insert, LatticeLimitEnum.Memory);
            Assert.False(grew);
            Assert.Null(pieces.Get(Inserts + 1L));
            Assert.Equal(before, ExpectAtRest(node, allocator).Charged);
        }
        else
        {
            await insert;
            Assert.True(grew);
            Assert.Equal(Inserts + 1L, pieces.Get(Inserts + 1L)?.Id);
            Assert.Equal(before + TableBytes(bucketBits + 1), ExpectAtRest(node, allocator).Charged);
        }
    }

    // The inserts before the one that grows the table a second time, and the key whose insert grew
    // it the first time. The retirement of the first table is freed, so the queue keeps room for
    // the next one.
    private static async Task<int> FillAsync(EngineDatabase node)
    {
        var pieces = node.Set<PieceSchema.Piece>();
        var growth = 0;
        var key = 0;
        node.ArmGrowthSwitch(pieces.EntityType, () => growth = key);
        for (key = 1; key <= Inserts; key++)
        {
            await pieces.InsertAsync(PieceSchema.Of(key, 8));
        }

        node.DrainRetired();
        return growth;
    }
}
