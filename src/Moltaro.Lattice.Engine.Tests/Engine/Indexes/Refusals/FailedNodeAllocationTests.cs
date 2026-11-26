using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;

// A preparation that fails after some indexes of its write reserved their nodes gives back what they
// reserved, its root and its root entry, and the instance stays Ready, as every preparation that fails
// does (Entities.ChangePublisher.TryReserve; Writes.WriteExecutor; FailedPreparationTests). The failure is
// the native allocator's, met when a later index needs a node from a new segment: the allocator of the
// test fails its next request (CountingNativeAllocator), and the OutOfMemoryException passes through.
// Paired has two indexes whose nodes lie in two size classes: ByNumber, of one Int64, in slots of 1 KiB,
// and ByWide, of four Strings, in larger ones. The arena has segments of one region and keeps none in
// reserve. The fill ends by a refusal of a filler and leaves no free slot of the class of the nodes of
// ByWide: WideFiller, whose texts take slots of that class, is written until the arena refuses one; then
// SpareFiller, whose text is the only block of a region of its own, is deleted, so that the arena has room
// for one segment. A write of Paired then takes the nodes of ByNumber from their region, which has room,
// and those of ByWide need that new segment. A replace and an insert each fail so: the call ends in that
// exception, the entity keeps its state or stays absent, a search finds it by its old keys and by no new
// one, and the database holds what it held (HeldState). The state compared with is taken after the same
// replace failed once, since what a pool of an index holds after a failure is the pool's to keep up to
// its reserve. Once the allocator works again, both writes succeed.
public sealed class FailedNodeAllocationTests
{
    private const int Region = Arena.MaxBlockSize;

    private const int Regions = 5;

    // Leave a fifth physical region for the injected allocation failure after the root fits.
    private static readonly ArenaOptions Options = new()
    {
        SegmentSize = Region,
        MemoryLimit = 4L * 1024 * 1024,
        NativeCapacityLimit = Regions * (long)Region,
        ReservedSegments = 0,
    };

    [Fact]
    public async Task Replace_and_insert_whose_later_index_meets_a_failed_native_allocation_give_back_what_the_earlier_index_reserved_and_leave_the_instance_Ready()
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await EngineMaster.OpenAsync(Schema, new EngineTestInputs { Arena = Options, Allocator = allocator, DisableBackgroundMaintenance = true });
        var paired = database.Set("Paired");
        var first = PairedOf(paired, 1, 1, "a");
        var replacement = PairedOf(paired, 1, 2, "e");
        var second = PairedOf(paired, 2, 3, "f");
        var wideNodes = NodeBytesOf("ByWide");

        // The premises: the nodes of ByNumber take slots of 1 KiB; those of ByWide and the texts of WideFiller
        // one larger class, and the text of SpareFiller another.
        Assert.Equal(IndexSchema.NodeBytes, NodeBytesOf("ByNumber"));
        Assert.True(wideNodes > IndexSchema.NodeBytes, "The nodes of ByWide take slots of 1 KiB.");
        Assert.Equal(wideNodes, Arena.BlockSizeFor(1100));
        Assert.NotEqual(wideNodes, Arena.BlockSizeFor(2000));

        await paired.InsertAsync(first);
        await FillAsync(database);

        Assert.Equal(Regions - 1, database.Counters.Arena.Segments);

        allocator.FailNextAllocation = true;
        await ExpectOutOfMemoryAsync(paired.ReplaceAsync(replacement).AsTask());
        var held = HeldState.Of(database);

        allocator.FailNextAllocation = true;
        await ExpectOutOfMemoryAsync(paired.ReplaceAsync(replacement).AsTask());

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        held.ExpectHeldBy(database);
        Assert.Equal(1L, paired.Get(1L)?["Number"]);
        Assert.Equal([1L], FoundBy(paired, first));
        Assert.Empty(FoundBy(paired, replacement));

        allocator.FailNextAllocation = true;
        await ExpectOutOfMemoryAsync(paired.InsertAsync(second).AsTask());

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        held.ExpectHeldBy(database);
        Assert.Null(paired.Get(2L));
        Assert.Empty(FoundBy(paired, second));

        await paired.ReplaceAsync(replacement);

        // The one block of the write that the arena had no room for: the replace obtained one segment.
        Assert.Equal(Regions, database.Counters.Arena.Segments);

        await paired.InsertAsync(second);

        Assert.Empty(FoundBy(paired, first));
        Assert.Equal([1L], FoundBy(paired, replacement));
        Assert.Equal([2L], FoundBy(paired, second));
    }

    // SpareFiller 1, then fillers of WideFiller until the arena refuses one, then SpareFiller deleted and
    // what it held drained.
    private static async Task FillAsync(EngineDatabase database)
    {
        var spare = database.Set("SpareFiller");
        var wide = database.Set("WideFiller");
        var filled = 0;

        await spare.InsertAsync(new LatticeRecord(spare.EntityType) { ["Id"] = 1L, ["Tag"] = new string('s', 2000) });

        for (var id = 1L; id <= 200; id++)
        {
            var refusal = await Record.ExceptionAsync(async () => await wide.InsertAsync(new LatticeRecord(wide.EntityType) { ["Id"] = id, ["Tag"] = $"{id:D4}".PadRight(1100, 'w') }));

            if (refusal is not null)
            {
                Assert.Equal(LatticeErrorEnum.WriteRejected, Assert.IsType<LatticeException>(refusal).Error);
                break;
            }

            filled++;
        }

        Assert.True(filled is > 0 and < 200, $"{filled} texts of WideFiller fitted the arena.");

        await spare.DeleteAsync(1L);
        database.DrainRetired();
    }

    // The bytes of a node of the index of Paired named.
    private static int NodeBytesOf(string index)
    {
        Assert.True(Schema.TryGetEntityType("Paired", out var type));
        Assert.True(type.TryGetIndex(index, out var definition));

        return IndexLayout.Compile(definition, RootLayout.Compile(type, Schema)).NodeBytes;
    }

    private static async Task ExpectOutOfMemoryAsync(Task write) =>
        await Assert.ThrowsAsync<OutOfMemoryException>(() => write.WaitAsync(Bound));

    private static LatticeRecord PairedOf(ILatticeSet paired, long id, long number, string firstWide) =>
        new(paired.EntityType) { ["Id"] = id, ["Number"] = number, ["W1"] = firstWide, ["W2"] = "b", ["W3"] = "c", ["W4"] = "d" };

    // The ids of the entities that a search of ByNumber and one of ByWide by the keys of the state given both
    // find, in ascending order; each search finds the same.
    private static List<long> FoundBy(ILatticeSet paired, LatticeRecord state)
    {
        var byNumber = paired.Index("ByNumber").Find(IndexKey.Equal(state.Get<long>("Number"))).Select(found => found.Get<long>("Id")).Order().ToList();
        var byWide = paired.Index("ByWide").Find(IndexKey.Equal(state.Get<string>("W1"), state.Get<string>("W2"), state.Get<string>("W3"), state.Get<string>("W4"))).Select(found => found.Get<long>("Id")).Order().ToList();

        Assert.Equal(byNumber, byWide);

        return byNumber;
    }

    // Paired: the key Id; Number, an Int64 in ByNumber, whose nodes take slots of 1 KiB; W1 to W4, Strings
    // in ByWide, whose four widest entries do not fit a node of 1 KiB, so its nodes take a larger class.
    // WideFiller and SpareFiller: the key Id and Tag, a String with Dictionary storage, whose texts lie in
    // entries of the text table, of 1,100 bytes in the class of the nodes of ByWide and of 2,000 bytes in a
    // class no other block takes.
    private static LatticeSchema Schema { get; } = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Paired",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Number", "Type": "Int64" },
                { "Id": 3, "Name": "W1", "Type": "String" },
                { "Id": 4, "Name": "W2", "Type": "String" },
                { "Id": 5, "Name": "W3", "Type": "String" },
                { "Id": 6, "Name": "W4", "Type": "String" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByNumber", "Fields": [2] }, { "Id": 2, "Name": "ByWide", "Fields": [3, 4, 5, 6] } ]
            },
            {
              "Id": 2, "Name": "WideFiller",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Tag", "Type": "String", "StringStorage": "Dictionary" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "SpareFiller",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Tag", "Type": "String", "StringStorage": "Dictionary" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);
}
