using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Budgeting;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Growth;

// A refused preparation returns what it reserved and every block, slot and text it prepared, and
// none of its data becomes visible; the capacity that the text table and a primary table grew for it
// stays with them, charged and reusable. The same write afterwards needs no growth and takes no new
// charge for one.
public sealed class RefusedGrowthTests
{
    private const long Limit = 64L * Segment;
    private const int Texts = 128;

    [Fact(Skip = Pending)]
    public async Task A_write_needing_the_growth_a_refused_write_made_takes_no_new_charge_and_the_refused_data_stays_invisible()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator, schema: IndexSchema.Utf8);
        var ranked = node.Set<IndexSchema.Ranked>();
        var uncounted = node.Set<IndexSchema.Uncounted>();
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Tables.Stored(3).Presize(1_024);
            return true;
        });
        for (var key = 1; key <= 8; key++)
        {
            await ranked.InsertAsync(Rank(key, $"region {key}"));
            await uncounted.InsertAsync(new IndexSchema.Uncounted { Id = key, Number = key });
        }

        node.DrainRetired();
        var initial = node.Position;
        var references = node.TextReferences().Count;
        var before = ExpectAtRest(node, allocator);

        IDisposable? held = null;
        try
        {
            node.ArmGrowthSwitch(uncounted.EntityType, () =>
                held = node.HoldBudgetBacking(checked((int)(Limit - node.InspectHeld().Charged - 32 - ArrayHeaderBytes))));
            var refused = Batch(node);
            refused.Insert(new IndexSchema.Ranked
            {
                Id = 10_000,
                Number = 10_000,
                Login = "large",
                Region = "large",
                Note = new string('n', 6 * 1024 * 1024),
            });
            await RefusedAsync(refused.CommitAsync().AsTask(), LatticeLimitEnum.Memory);
            Assert.NotNull(held);
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
        Assert.Equal(references, node.TextReferences().Count);
        Assert.Equal(16, node.Counters.RootEntries);
        OwnershipAudit.Expect(node);
        var rest = ExpectAtRest(node, allocator);
        Assert.NotEmpty(Added(rest.Arrays, before.Arrays));

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

    private static IndexSchema.Ranked Rank(int key, string region) =>
        new() { Id = key, Number = key, Login = $"login {key}", Region = region };

    private static string NewRegion(int key) => $"new region {key}";
}
