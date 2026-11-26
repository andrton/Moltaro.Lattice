using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class BudgetInventorySelectionTests
{
    [Fact]
    public async Task The_same_seeded_indexed_and_unindexed_writes_keep_their_outcomes_and_charges_with_inventory_disabled()
    {
        var observed = await TraceAsync(observe: true);
        var unobserved = await TraceAsync(observe: false);
        Assert.NotEmpty(observed);
        Assert.Equal(observed, unobserved);
    }

    private static async Task<Replacement[]> TraceAsync(bool observe)
    {
        var allocator = new CountingNativeAllocator();
        var inputs = new EngineTestInputs
        {
            IndexSeed = 6035,
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            ObserveBudgetBackings = observe,
        };

        await using var node = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            Schema = IndexSchema.Utf8,
            MemoryLimit = 8L * Segment,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        }, inputs);
        await node.OpenAsync().AsTask().WaitAsync(Bound);
        var counted = node.Set<IndexSchema.Counted>();
        var uncounted = node.Set<IndexSchema.Uncounted>();
        await counted.InsertAsync(new IndexSchema.Counted { Id = 1L, Number = 2L });
        await uncounted.InsertAsync(new IndexSchema.Uncounted { Id = 1L, Number = 2L });
        var snapshots = new List<Replacement>();

        foreach (var value in new[] { 2L, 3L, 4L })
        {
            var sample = default(BudgetCapacitySample);
            node.ResetBudgetPeak();
            node.ArmPublicationSwitch(PublicationPointEnum.BeforePosition, () => sample = node.InspectBudget());
            var prior = node.Position;
            var indexed = await counted.ReplaceAsync(new IndexSchema.Counted { Id = 1L, Number = value }).AsTask().WaitAsync(Bound);
            Assert.Equal(prior.Sequence + 1, indexed.Position.Sequence);
            Assert.Equal(indexed.Position, node.Position);
            Assert.Equal(1L, Assert.Single(counted.Index("ByNumber").Find(IndexKey.Equal(value))).Id);
            Assert.True(sample.ChargedBytes > 0);
            snapshots.Add(new Replacement(indexed.Position.Sequence, value, sample, Inspect()));

            sample = default;
            node.ResetBudgetPeak();
            node.ArmPublicationSwitch(PublicationPointEnum.BeforePosition, () => sample = node.InspectBudget());
            var plain = await uncounted.ReplaceAsync(new IndexSchema.Uncounted { Id = 1L, Number = value }).AsTask().WaitAsync(Bound);
            Assert.Equal(indexed.Position.Sequence + 1, plain.Position.Sequence);
            Assert.Equal(plain.Position, node.Position);
            Assert.Equal(value, uncounted.Get(1L)?.Number);
            Assert.True(sample.ChargedBytes > 0);
            snapshots.Add(new Replacement(plain.Position.Sequence, value, sample, Inspect()));
        }

        return [.. snapshots];

        BudgetCapacitySample Inspect()
        {
            var owners = new List<object>();
            var sample = node.InspectBudget((_, backing) => owners.Add(backing));
            if (observe)
            {
                Assert.NotEmpty(owners);
                Assert.Equal(sample, ExpectCapacity(node, allocator, out _, owners.ToArray()));
            }
            else
            {
                Assert.Empty(owners);
                Assert.Equal(allocator.OutstandingBytes, sample.NativeBytes);
            }

            return sample;
        }
    }

    private sealed record Replacement(ulong Sequence, long Number, BudgetCapacitySample BeforePublication, BudgetCapacitySample AfterPublication);
}
