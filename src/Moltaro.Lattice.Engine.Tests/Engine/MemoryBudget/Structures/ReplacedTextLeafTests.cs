using System.Reflection;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Text;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// A leaf of the locator of the text table is replaced by a copy when its IDs outgrow it or leave it half
// empty, or dropped when they are gone, and a branch of its pages is dropped once its pages are gone; both
// are released at once, though a read may still hold them. A read holds at most one of each at a time, so
// the headroom of a schema whose texts the text table holds has one largest leaf and one branch for each
// slot of the protection of reads (docs/design/storage-and-maintenance.md, the memory budget). With a read
// held on every slot and, for each, a dropped leaf of the largest size kept as that read could keep it, the
// inventory and those leaves stay within the charge; once the reads have left and the leaves are let go,
// the balance at rest is exact again. The test keeps no dropped branch, which would take texts on more
// than the 32 pages of the first branch.
public sealed class ReplacedTextLeafTests
{
    private const long Limit = 64L * Segment;

    // A leaf maps the IDs of one page, and has room for all of them once more than half are taken.
    private const int IdsPerLeaf = 64;

    // The IDs of the first page, whose ID 0 is no text.
    private const int PageIds = IdsPerLeaf - 1;

    // A branch of the pages has a child for each of 32 of them.
    private const int BranchChildren = 32;

    [Fact]
    public async Task Reads_on_every_slot_each_keeping_a_replaced_leaf_stay_within_the_charge()
    {
        var schema = TextSchema.Utf8;
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator, schema: schema);
        var depots = node.Set<TextSchema.Depot>();
        var slots = new ReadEpochs(new object()).SlotCount;

        // The headroom, from the slot count and the layouts as the tests size them: the measured parts, the
        // slots, and one largest leaf with one branch for each slot.
        var largestLeaf = ObjectBytes(typeof(TextLocator).GetNestedType("Leaf", BindingFlags.NonPublic)!) +
            ArrayBytes(typeof(long), IdsPerLeaf) + ArrayBytes(typeof(int), IdsPerLeaf);
        var branch = ObjectBytes(typeof(TextPages<>).GetNestedType("Branch", BindingFlags.NonPublic)!) + ArrayBytes(typeof(object), BranchChildren);
        var types = schema.EntityTypes.Count;
        var indexes = types + schema.EntityTypes.Sum(entityType => entityType.Indexes.Count);
        var parts = Headroom.InstanceBytes + types * Headroom.EntityTypeBytes + indexes * Headroom.IndexBytes;
        Assert.Equal(parts + ArrayBytes(typeof(EpochSlot), slots) + slots * (largestLeaf + branch), node.InspectHeld().Headroom);

        // The first page of the locator maps the IDs 1 to 63, which the texts take in order: 63 texts give
        // it a leaf with room for 64, and the leaf is dropped once every text is gone. Each round keeps the
        // largest leaf, with its arrays, as a read that entered before the drop could keep it.
        var kept = new List<IReadOnlyList<TextLocatorAllocation>>();
        for (var round = 0; round < slots; round++)
        {
            var first = round * PageIds + 1L;
            await WriteAsync(node, first, delete: false);
            var leaf = Leaf(node, 1)!;
            Assert.Equal(IdsPerLeaf, leaf[1].Shape.ArrayLength);
            await WriteAsync(node, first, delete: true);
            for (var drain = 0; drain < 4 && Leaf(node, 1) is not null; drain++)
            {
                node.DrainRetired();
            }

            Assert.Null(Leaf(node, 1));
            kept.Add(leaf);
        }

        var keeper = slots * PageIds + 1L;
        await depots.InsertAsync(new TextSchema.Depot { Id = keeper, Area = "kept" }).AsTask().WaitAsync(Bound);
        ExpectAtRest(node, allocator);

        // A read on every slot, each with the leaf of one round: the inventory and those leaves stay within
        // the charge.
        var reads = new List<HeldRead>();
        try
        {
            for (var read = 0; read < slots; read++)
            {
                reads.Add(await HeldRead.StartAsync(node, () => depots.Get(keeper)?.Id));
            }

            var held = node.InspectHeld();
            Assert.InRange(HeldInventory.Inventory(held) + kept.Count * largestLeaf, 0, held.Charged);
        }
        finally
        {
            foreach (var read in reads)
            {
                await read.ReleaseAsync();
            }
        }

        GC.KeepAlive(kept);
        kept.Clear();
        ExpectAtRest(node, allocator);
    }

    // Inserts the 63 depots from the first given, each with a text of its own, or deletes them, as one batch.
    private static Task WriteAsync(EngineDatabase node, long first, bool delete)
    {
        var batch = node.CreateWriteBatch();
        for (var id = first; id < first + PageIds; id++)
        {
            if (delete)
            {
                batch.Delete<TextSchema.Depot>(id);
            }
            else
            {
                batch.Insert(new TextSchema.Depot { Id = id, Area = $"area {id}" });
            }
        }

        return batch.CommitAsync().AsTask().WaitAsync(Bound);
    }

    // The leaf that holds the ID, with its two arrays, as the locator has it now; null when the ID is not taken.
    private static IReadOnlyList<TextLocatorAllocation>? Leaf(EngineDatabase node, uint id)
    {
        IReadOnlyList<TextLocatorAllocation>? leaf = null;
        Assert.True(node.TryRunMaintenanceStep(parts => leaf = parts.Arena.Texts.TryCaptureLocatorLeaf(id, out var found) ? found : null));
        return leaf;
    }
}
