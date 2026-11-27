using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Child;

// A transaction a child applies is a write unit: it reserves the base of a unit (PerUnitBytes) and
// PerMemberBytes for each of its effects before it prepares the first, and releases it once it has ended. The estimate is read under the lock
// of the writes of the child, at the point before the first effect is prepared, where nothing else of
// the transaction is charged yet: there the charge exceeds the inventory and the headroom by it.
public sealed class ChildTransactionEstimateTests
{
    private const long Limit = 64L * Segment;

    [Fact]
    public async Task A_child_transaction_reserves_the_base_of_a_unit_and_PerMemberBytes_for_each_member_when_it_starts_and_releases_it_when_it_ends()
    {
        await using var master = await OpenAsync(Limit, new CountingNativeAllocator(), listen: true, schema: IndexSchema.Utf8);
        var seeded = await master.Set<IndexSchema.Counted>().InsertAsync(new IndexSchema.Counted { Id = 1, Number = 1 });
        var allocator = new CountingNativeAllocator();
        EngineDatabase? child = null;
        long? inside = null;
        var armed = 0;
        child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(master.ListenUri!, ReplicationNodes.BeyondTheBound, Limit),
            new EngineTestInputs
            {
                Allocator = allocator,
                DisableBackgroundMaintenance = true,
                BeforeChildPreparationMember = member =>
                {
                    if (member == 1 && Interlocked.Exchange(ref armed, 0) == 1)
                    {
                        var held = child!.InspectHeld();
                        inside = held.Charged - (HeldInventory.Inventory(held) + held.Headroom);
                    }
                },
            });
        await using var owned = child;
        await child.WaitForPositionAsync(seeded.Position, Bound);
        child.DrainRetired();
        ExpectAtRest(child, allocator);

        Volatile.Write(ref armed, 1);
        var batch = master.CreateWriteBatch();
        batch.Insert(new IndexSchema.Counted { Id = 2, Number = 2 });
        batch.Insert(new IndexSchema.Counted { Id = 3, Number = 3 });
        batch.Replace(new IndexSchema.Counted { Id = 1, Number = 10 });
        var written = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(written.Position, Bound);

        // At rest again the charge is the inventory and the headroom: the estimate was released.
        Assert.Equal(UnitEstimate(3), inside);
        ExpectAtRest(child, allocator);
    }
}
