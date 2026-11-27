using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Collections;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// What a preparation changes in the collection tails stays in maps that the budget does not charge,
// working objects under the estimate of the write (docs/design/storage-and-maintenance.md, the memory
// budget). They must not keep, after the write, what a batch of many roots made them grow: between
// writes each holds no more than the capacity a map takes for its first entry.
public sealed class CollectionTailMapsTests
{
    private const int Items = 8;
    private const int Full = 9_000;
    private const int Small = 5;
    private const int Chunk = 65_536;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int32" },
                { "Id": 2, "Name": "Values", "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "Int32" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact]
    public async Task A_batch_that_regrows_many_shrunk_maps_leaves_the_maps_of_the_tails_at_the_capacity_of_their_first_entry()
    {
        await using var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = Schema,
        }, new EngineTestInputs
        {
            Allocator = new CountingNativeAllocator(),
            Arena = new ArenaOptions { MemoryLimit = 64L * 1024 * 1024, SegmentSize = Chunk, ReservedSegments = 0 },
            DisableBackgroundMaintenance = true,
            MaintenanceTimeProvider = new CollectionMaintenanceTime(),
        });
        await database.OpenAsync().AsTask().WaitAsync(Bound);
        var items = database.Set("Item");
        for (var key = 1; key <= Items; key++)
        {
            var values = Enumerable.Range(0, Full).ToDictionary(index => index, index => index);
            await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = key, ["Values"] = values }).AsTask().WaitAsync(Bound);
            await Resize(items, key, Full, Small).CommitAsync().AsTask().WaitAsync(Bound);
        }

        // The tails the shrinks left become ready to be borrowed.
        for (var turn = 0; turn < 12; turn++)
        {
            Assert.True(SpinWait.SpinUntil(database.TryRunMaintenanceTurn, Bound));
        }

        var ready = Leases(database);
        Assert.True(ready >= Items, $"The shrinks left {ready} leases.");

        var batch = database.CreateWriteBatch();
        for (var key = 1; key <= Items; key++)
        {
            batch.Update(Resize(items, key, Small, Full));
        }

        await batch.CommitAsync().AsTask().WaitAsync(Bound);

        Assert.True(Leases(database) < ready, "The regrowths borrowed no slot of the tails.");
        var first = new Dictionary<int, int> { [0] = 0 }.EnsureCapacity(0);
        CollectionTails tails = null!;
        Assert.True(SpinWait.SpinUntil(() => database.TryRunMaintenanceStep(parts => tails = parts.Arena.CollectionTails!), Bound));
        Assert.InRange(Backing<Dictionary<ArenaAddress, Dictionary<int, CollectionTailEdit>?>>(tails, "_preparations").EnsureCapacity(0), 0, first);
        Assert.InRange(Backing<Dictionary<ArenaAddress, int>>(tails, "_borrowed").EnsureCapacity(0), 0, first);
    }

    private static LatticeUpdate Resize(ILatticeSet items, int key, int before, int after)
    {
        var update = items.CreateUpdate(key);
        for (var index = Math.Min(before, after); index < Math.Max(before, after); index++)
        {
            _ = before > after ? update.MapRemove("Values", index) : update.MapSet("Values", index, index);
        }

        return update;
    }

    private static int Leases(EngineDatabase database)
    {
        var leases = 0;
        Assert.True(SpinWait.SpinUntil(() => database.TryRunMaintenanceStep(parts => leases = parts.Arena.CollectionSlotOwnershipForTest().Length), Bound));
        return leases;
    }
}
