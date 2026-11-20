using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// Once a write has touched an index, its pool keeps a standing reserve of nodes, the need of one change of
// key at the tree as it is (IndexReserve), instead of being topped up and trimmed on every write; it is
// taken by the first preparation that touches the index, not at the open, and is not given back before
// the database closes (docs/design/ordered-index.md, "The reservation"). It is never the reason of a
// refusal, since a write is refused for memory only when it does not fit beside the stored data (decision
// on write admission, Q14): a preparation takes the need of its write and then tops the pool up to the
// reserve as far as the arena gives, and before a write is refused every index gives back what its pool
// holds above the need of the writes still prepared, and the write is tried once more with its own need
// and no top-up.
// - after every write of an indexed type is published, the prepared bytes of the arena,
//   Counters.Arena.PreparedBytes, are above zero and at most the reserve of its indexes, through a tree
//   that grows to two levels and empties again; before the first such write, and after a write of a type
//   without an index, they are zero;
// - the reserve counts against the memory limit while it is held, and a write that fits only without the
//   reserves succeeds: in the arena of FullArena, the first entity of Ranked takes the reserves of its
//   indexes, and while they are held the slots of 1 KiB of the region of the nodes hold the nodes of the
//   trees, the reserves and the texts of the fillers, no more. The filler that finds no slot of that class
//   beside the reserves is written all the same, the reserves given back, after which nothing is
//   prepared, below the reserves, and the fillers take every slot the trees do not;
// - a close gives it back: once the close of a database that holds a reserve has completed, every piece
//   of native memory it obtained is returned, exactly once (CountingNativeAllocator);
// - a database whose memory limit is below the reserves of its many indexes opens, writes a first entity
//   that fits by itself though the reserves cannot be taken, and refuses for memory, with nothing left
//   taken, a write whose own need cannot be had. Spread has forty indexes of one Int64 each, in an arena of
//   one region for its roots and one for its nodes, sixty-four slots of 1 KiB. The first insert into forty
//   empty trees takes a leaf in each, forty slots; the reserve of each index, the need of an insert and a
//   remove, is more than one node, so the reserves do not fit beside the leaves, and the pools hold what
//   the arena could give. The second insert may split each leaf under a new root, three nodes an index and
//   a hundred and twenty in all, more than the region holds beside the leaves.
public sealed class StandingReserveTests
{
    private const int SpreadIndexes = 40;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task After_every_published_write_of_an_indexed_type_the_arena_holds_prepared_bytes_above_zero_and_within_the_reserve_of_its_indexes()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        Assert.Equal(0, database.Counters.Arena.PreparedBytes);

        await database.Set<IndexSchema.Uncounted>().InsertAsync(new IndexSchema.Uncounted { Id = 1, Number = 1 });

        Assert.Equal(0, database.Counters.Arena.PreparedBytes);

        for (var id = 1L; id <= 200; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
            ExpectWithinTheReserve(database);
        }

        Assert.True(IndexContents.Of(database, "Counted", "ByNumber").Height > 1, "The inserts left the tree one leaf.");

        for (var id = 1L; id <= 200; id++)
        {
            await counted.DeleteAsync(id);
            ExpectWithinTheReserve(database);
        }
    }

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task Reserve_counts_against_the_memory_limit_while_it_is_held_and_a_write_that_fits_only_without_it_succeeds()
    {
        await using var database = await EngineMaster.OpenAsync(FullArena.Schema, new EngineTestInputs { Arena = FullArena.Options });
        var fillers = database.Set("Filler");

        await database.Set<IndexSchema.Ranked>().InsertAsync(FullArena.First);

        var nodes = new[] { "ByNumber", "ByLogin", "ByRegion" }.Sum(index => IndexContents.Of(database, "Ranked", index).Nodes);
        var prepared = database.Counters.Arena.PreparedBytes;
        var filled = 0;
        var givenBack = false;

        Assert.True(prepared > 0, "The first write of the indexes took no reserve.");

        for (var id = 1L; id <= 200; id++)
        {
            var refusal = await Record.ExceptionAsync(async () => await fillers.InsertAsync(new LatticeRecord(fillers.EntityType) { ["Id"] = id, ["Tag"] = $"{id:D4}".PadRight(992, 't') }));

            if (refusal is not null)
            {
                Assert.Equal(LatticeErrorEnum.WriteRejected, Assert.IsType<LatticeException>(refusal).Error);
                break;
            }

            filled++;

            var held = prepared;

            prepared = database.Counters.Arena.PreparedBytes;
            givenBack |= held > 0 && prepared == 0;

            // While the reserves are held, they and the fillers share the slots of the region.
            Assert.True(prepared == 0 || filled + nodes + (prepared / IndexSchema.NodeBytes) <= FullArena.SlotsOfOneKiB, $"{filled} fillers, {nodes} nodes and {prepared} prepared bytes in a region of {FullArena.SlotsOfOneKiB} slots.");
        }

        FullArena.ExpectFull(database);
        Assert.True(givenBack, "No filler was written in a slot the reserves had held.");
        // Below their reserve: the pools hold nothing once they gave it back.
        Assert.Equal(0, prepared);
        Assert.Equal(FullArena.SlotsOfOneKiB, filled + nodes);
    }

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task Close_of_a_database_that_holds_a_reserve_returns_every_piece_of_native_memory()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, new EngineTestInputs { Allocator = allocator });

        await database.Set<IndexSchema.Ranked>().InsertAsync(new IndexSchema.Ranked { Id = 1, Number = 1, Login = new string('a', 100), Region = "North" });

        Assert.True(database.Counters.Arena.PreparedBytes > 0, "The write left no reserve.");

        await database.CloseAsync().AsTask().WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task Database_whose_memory_limit_is_below_the_reserves_of_its_indexes_opens_writes_what_fits_by_itself_and_refuses_with_nothing_left_taken_what_does_not()
    {
        var arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 2L * Arena.MaxBlockSize, ReservedSegments = 0 };

        await using var database = await EngineMaster.OpenAsync(SpreadSchema, new EngineTestInputs { Arena = arena });
        var spread = database.Set("Spread");

        await spread.InsertAsync(SpreadOf(spread, 1));

        var afterFirst = database.Counters;

        Assert.True(afterFirst.Arena.PreparedBytes > 0, "The pools took nothing of what the arena could give.");
        Assert.True(afterFirst.Arena.PreparedBytes < IndexReserve.BytesOf(database, "Spread"), "The pools hold the whole of their reserves.");

        await FullArena.ExpectRefusedAsync(spread.InsertAsync(SpreadOf(spread, 2)).AsTask());

        var afterRefusal = database.Counters;

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        Assert.Equal(0, afterRefusal.Arena.PreparedBytes);
        Assert.Equal((afterFirst.Arena.LiveBytes, afterFirst.Arena.ClassSlackBytes, afterFirst.RootEntries), (afterRefusal.Arena.LiveBytes, afterRefusal.Arena.ClassSlackBytes, afterRefusal.RootEntries));
        Assert.NotNull(spread.Get(1L));
        Assert.Null(spread.Get(2L));
    }

    // An entity of Spread with the id given, each value of its own.
    private static LatticeRecord SpreadOf(ILatticeSet spread, long id)
    {
        var record = new LatticeRecord(spread.EntityType) { ["Id"] = id };

        for (var field = 1; field <= SpreadIndexes; field++)
        {
            record[$"Value{field}"] = (id * 1_000) + field;
        }

        return record;
    }

    private static void ExpectWithinTheReserve(EngineDatabase database)
    {
        var prepared = database.Counters.Arena.PreparedBytes;
        var reserve = IndexReserve.BytesOf(database, "Counted");

        Assert.True(prepared > 0 && prepared <= reserve, $"The arena holds {prepared} prepared bytes after a published write, with a reserve of {reserve}.");
    }

    // Spread: the key Id and forty Int64 fields, each in an index of its own.
    private static LatticeSchema SpreadSchema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Spread",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }{{SpreadFields()}} ],
              "PrimaryKey": [1],
              "Indexes": [ {{SpreadIndexDeclarations()}} ]
            }
          ]
        }
        """);

    private static string SpreadFields() =>
        string.Concat(Enumerable.Range(1, SpreadIndexes).Select(field => $$""", { "Id": {{field + 1}}, "Name": "Value{{field}}", "Type": "Int64" }"""));

    private static string SpreadIndexDeclarations() =>
        string.Join(", ", Enumerable.Range(1, SpreadIndexes).Select(field => $$"""{ "Id": {{field}}, "Name": "ByValue{{field}}", "Fields": [{{field + 1}}] }"""));
}
