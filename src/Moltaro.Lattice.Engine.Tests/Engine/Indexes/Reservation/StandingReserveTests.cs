using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// Once a write has touched an index, its pool keeps a standing reserve of nodes, the need of one change of
// key at the tree as it is (IndexReserve), instead of being topped up and trimmed on every write; it is
// taken by the first preparation that touches the index, not at the open, and is not given back before
// the database closes (docs/design/ordered-index.md, "The reservation"):
// - after every write of an indexed type is published, the prepared bytes of the arena,
//   Counters.Arena.PreparedBytes, are above zero and at most the reserve of its indexes, through a tree
//   that grows to two levels and empties again; before the first such write, and after a write of a type
//   without an index, they are zero;
// - the reserve counts against the memory limit like any other bytes: in an arena filled to its limit
//   beside one entity of Ranked (FullArena), the slots of 1 KiB of the region of nodes hold the nodes of
//   the trees, the reserve and the texts of the fillers, and no filler had a slot of the reserve;
// - a close gives it back: once the close of a database that holds a reserve has completed, every piece
//   of native memory it obtained is returned, exactly once (CountingNativeAllocator);
// - a database whose memory limit is below the reserves of its many indexes opens, and its first write is
//   the refusal for memory, with nothing left taken (HeldState). Spread has forty indexes of one Int64
//   each, in an arena of one region for its root and one for its nodes, sixty-four slots of 1 KiB. The
//   reserve of each index is more than one node, since a change of key is an insert and a remove, so the
//   reserves do not fit, while the one node of each that the insert of a first write takes would.
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
    public async Task Reserve_takes_slots_of_the_arena_that_no_other_write_gets_when_the_arena_is_at_its_memory_limit()
    {
        await using var database = await EngineMaster.OpenAsync(FullArena.Schema, new EngineTestInputs { Arena = FullArena.Options });
        var fillers = await FullArena.FillAsync(database);

        database.DrainRetired();
        var prepared = database.Counters.Arena.PreparedBytes;
        var nodes = new[] { "ByNumber", "ByLogin", "ByRegion" }.Sum(index => IndexContents.Of(database, "Ranked", index).Nodes);

        FullArena.ExpectFull(database);
        Assert.True(prepared > 0, "The arena refused a filler with no reserve prepared.");
        Assert.Equal(FullArena.SlotsOfOneKiB, fillers + nodes + (prepared / IndexSchema.NodeBytes));
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
    public async Task Database_whose_memory_limit_is_below_the_reserves_of_its_indexes_opens_and_refuses_its_first_write_for_memory_with_nothing_left_taken()
    {
        var arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 2L * Arena.MaxBlockSize, ReservedSegments = 0 };

        await using var database = await EngineMaster.OpenAsync(SpreadSchema, new EngineTestInputs { Arena = arena });
        var spread = database.Set("Spread");
        var first = new LatticeRecord(spread.EntityType) { ["Id"] = 1L };

        for (var field = 1; field <= SpreadIndexes; field++)
        {
            first[$"Value{field}"] = (long)field;
        }

        var held = HeldState.Of(database);

        await FullArena.ExpectRefusedAsync(spread.InsertAsync(first).AsTask());

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        held.ExpectHeldBy(database);
        Assert.Null(spread.Get(1L));
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
