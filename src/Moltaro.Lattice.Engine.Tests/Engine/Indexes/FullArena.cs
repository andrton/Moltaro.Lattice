using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// An arena that holds all it may beside one entity of Ranked, so that a later write of Ranked finds a place
// for its root and its texts and none for the nodes its indexes need. The arena has segments of one region,
// 64 KiB, each region serving one size class, and five of them, none kept in reserve (EngineTestInputs,
// exception X3). The first entity of Ranked takes a region for its root, one for the text entry of its long
// Login, one for the entry of its short Region and one for the nodes of its indexes, a leaf of 1 KiB in each;
// then the texts of Filler, a Dictionary field of 992 bytes whose entries take blocks of 1 KiB too, fill the
// region of the nodes beside the region of their own roots, until the arena refuses one. A filler deleted,
// once what it held is drained, gives back one slot of 1 KiB.
//
// The premise of every refusal of a master that these tests bring about: the fill ends by a refusal of a
// filler and leaves no free slot of the class of the nodes. A write is refused for memory only when it
// does not fit beside the stored data (decision on write admission, Q14), so before the refusal every
// index gives back what its pool holds above the need of the writes prepared, none here, and the filler
// is tried once more: what a pool held, the refused filler found free, and after the fill the pools hold
// nothing beside the nodes of the trees.
internal static class FullArena
{
    public const int Regions = 5;

    private const int Region = Arena.MaxBlockSize;

    // The slots of 1 KiB of one region, the size of a node and of the text entry of a filler.
    public const int SlotsOfOneKiB = Region / IndexSchema.NodeBytes;

    // A fixed number of whole regions makes the allocator full even when a managed root page fits.
    public static ArenaOptions Options { get; } = new()
    {
        SegmentSize = Region, MemoryLimit = 32L * 1024 * 1024, NativeCapacityLimit = Regions * (long)Region, ReservedSegments = 0,
    };

    // The schema of IndexSchema with the entity type Filler: the key Id and Tag, a String with Dictionary
    // storage, whose text lies in an entry of the text table.
    public static LatticeSchema Schema { get; } = IndexSchema.With("Utf8", """
        {
          "Id": 10, "Name": "Filler",
          "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Tag", "Type": "String", "StringStorage": "Dictionary" } ],
          "PrimaryKey": [1]
        },
        """);

    // The entity of Ranked written before the fillers.
    public static IndexSchema.Ranked First => new() { Id = 1, Number = 1, Login = new string('a', 100), Region = "North" };

    // An entity of Ranked written into the full arena, with keys of its own in each index: a long Login and
    // a Region that are new, whose root and text entries fit the regions First took.
    public static IndexSchema.Ranked Second => new() { Id = 2, Number = 2, Login = new string('b', 100), Region = "South" };

    // Opens a master for rollback tests, writes First and fills the arena. The tests take snapshots
    // after explicit drains and isolate them from independent maintenance.
    public static async Task<EngineDatabase> OpenAsync()
    {
        var database = await EngineMaster.OpenAsync(Schema, new EngineTestInputs { Arena = Options, DisableBackgroundMaintenance = true });

        await FillAsync(database);

        return database;
    }

    // Writes First and then fillers from the id 1 on until the arena refuses one, and returns how many it
    // holds.
    public static async Task<int> FillAsync(ILatticeDatabase database)
    {
        await database.Set<IndexSchema.Ranked>().InsertAsync(First);

        var fillers = database.Set("Filler");
        var filled = 0;

        for (var id = 1L; id <= 200; id++)
        {
            var refusal = await Record.ExceptionAsync(async () => await fillers.InsertAsync(FillerOf(fillers, id)));

            if (refusal is not null)
            {
                Assert.Equal(LatticeErrorEnum.WriteRejected, Assert.IsType<LatticeException>(refusal).Error);
                break;
            }

            filled++;
        }

        Assert.True(filled > 0, "No text of 1 KiB fitted the arena.");
        Assert.True(filled < 200, "The arena held every text of 1 KiB.");

        return filled;
    }

    // Writes the fillers from the id 1 to the count given, into a database whose arena is not this one.
    public static async Task InsertFillersAsync(ILatticeDatabase database, int count)
    {
        var fillers = database.Set("Filler");

        for (var id = 1L; id <= count; id++)
        {
            await fillers.InsertAsync(FillerOf(fillers, id));
        }
    }

    // Deletes the fillers from the id first to the id last and drains what they held: a slot of 1 KiB each.
    public static async Task FreeAsync(EngineDatabase database, long first, long last)
    {
        var fillers = database.Set("Filler");

        for (var id = first; id <= last; id++)
        {
            await fillers.DeleteAsync(id);
        }

        database.DrainRetired();
    }

    // Every region of the arena of the database is taken, and none is free.
    public static void ExpectFull(EngineDatabase database)
    {
        var arena = database.Counters.Arena;

        Assert.Equal((Regions, 0), (arena.Segments, arena.FreeRegions));
    }

    // The write ends, within the bound, refused as a write the memory budget cannot hold.
    public static async Task ExpectRefusedAsync(Task write)
    {
        var refused = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
    }

    private static LatticeRecord FillerOf(ILatticeSet fillers, long id) => new(fillers.EntityType) { ["Id"] = id, ["Tag"] = $"{id:D4}".PadRight(992, 't') };
}
