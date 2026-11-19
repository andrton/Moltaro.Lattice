using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Index nodes are memory of the arena, counted in the budget of the instance, and a reservation of them
// that the arena cannot hold takes nothing and is refused as a write the memory budget cannot hold is:
// before its commit (docs/design/ordered-index.md, "The reservation"; decision on write admission, Q14:
// WriteRejected with Limit Memory, and the write has had no effect).
//
// The arena of these tests has segments of one region, 64 KiB, each region serving one size class, and
// five of them (EngineTestInputs, exception X3). The first write of Ranked takes a region for its root, one
// for the text entry of its long Login, one for the entry of its short Region and one for the nodes of its
// indexes, which are blocks of 1 KiB; then texts of a Dictionary field of 992 bytes, which take blocks of
// 1 KiB too, fill the region of the nodes beside the region of their own roots, until the arena refuses
// one. A write of Ranked then finds a place for its root and for its long Login, and none for the
// nodes its indexes need: it is WriteRejected with Limit Memory and leaves nothing behind — no root
// entry, no prepared, live or retired byte, no native segment, no text entry and no reference, nothing
// found by a search; every counter is as before — and the instance stays Ready. One thing it may leave
// grown: the managed capacity of the text table, whose bucket table a new entry may have grown before the
// arena refused the entry, and which keeps that room for the next one (Storage.Text.TextTable). That
// capacity is reported and not charged to the memory budget (EngineCounters, TextTableBytes); it never
// shrinks for the refusal. Once memory is freed, the same write succeeds and is found.
public sealed class RefusedReservationTests
{
    private const int Region = Arena.MaxBlockSize;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Write_whose_index_nodes_the_arena_cannot_hold_is_refused_for_memory_leaves_nothing_and_succeeds_once_memory_is_freed()
    {
        var arena = new ArenaOptions { SegmentSize = Region, MemoryLimit = 5L * Region, ReservedSegments = 0 };

        await using var database = await EngineMaster.OpenAsync(FillerSchema, new EngineTestInputs { Arena = arena });
        var ranked = database.Set<IndexSchema.Ranked>();
        var fillers = database.Set("Filler");

        await ranked.InsertAsync(new IndexSchema.Ranked { Id = 1, Number = 1, Login = new string('a', 100), Region = "North" });

        var filled = 0;

        for (var id = 1L; id <= 200; id++)
        {
            var refusal = await Record.ExceptionAsync(async () => await fillers.InsertAsync(new LatticeRecord(fillers.EntityType) { ["Id"] = id, ["Tag"] = $"{id:D4}".PadRight(992, 't') }));

            if (refusal is not null)
            {
                Assert.Equal(LatticeErrorEnum.WriteRejected, Assert.IsType<LatticeException>(refusal).Error);
                break;
            }

            filled++;
        }

        Assert.True(filled > 0, "No text of 1 KiB fitted the arena.");
        Assert.True(filled < 200, "The arena held every text of 1 KiB.");

        database.DrainRetired();
        var before = database.Counters;
        var texts = database.TextReferences();

        // The premise: the arena holds all it may, and no region is free.
        Assert.Equal((5, 0), (before.Arena.Segments, before.Arena.FreeRegions));

        var refused = await Assert.ThrowsAsync<LatticeException>(() => ranked.InsertAsync(new IndexSchema.Ranked { Id = 2, Number = 2, Login = new string('b', 100), Region = "North" }).AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);

        database.DrainRetired();
        var after = database.Counters;

        Assert.Equal(before with { TextTableBytes = after.TextTableBytes }, after);
        Assert.True(after.TextTableBytes >= before.TextTableBytes, $"The text table held {before.TextTableBytes} managed bytes before the refusal and {after.TextTableBytes} after it.");
        Assert.Equal(texts, database.TextReferences(), TextEntryComparer.Instance);
        Assert.Null(ranked.Get(2L));
        Assert.Empty(ranked.Index("ByNumber").Find(IndexKey.Equal(2L)));
        Assert.Empty(ranked.Index("ByLogin").Find(IndexKey.Equal(new string('b', 100))));
        Assert.Equal(1, Assert.Single(ranked.Index("ByRegion").Find(IndexKey.Equal("north"))).Id);

        // Memory freed: ten texts of 1 KiB go.
        for (var id = 1L; id <= 10; id++)
        {
            await fillers.DeleteAsync(id);
        }

        database.DrainRetired();

        await ranked.InsertAsync(new IndexSchema.Ranked { Id = 2, Number = 2, Login = new string('b', 100), Region = "North" });

        Assert.Equal(2, Assert.Single(ranked.Index("ByNumber").Find(IndexKey.Equal(2L))).Id);
        Assert.Equal(2, Assert.Single(ranked.Index("ByLogin").Find(IndexKey.Equal(new string('b', 100)))).Id);
        Assert.Equal([1L, 2L], ranked.Index("ByRegion").Find(IndexKey.Equal("NORTH")).Select(entity => entity.Id).Order());
    }

    // The schema of IndexSchema with the entity type Filler: the key Id and Tag, a String with Dictionary
    // storage, whose text lies in an entry of the text table.
    private static LatticeSchema FillerSchema { get; } = IndexSchema.With("Utf8", """
        {
          "Id": 10, "Name": "Filler",
          "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Tag", "Type": "String", "StringStorage": "Dictionary" } ],
          "PrimaryKey": [1]
        },
        """);

    // Two lists of text entries are equal when they hold the same texts with the same references, in any
    // order.
    private sealed class TextEntryComparer : IEqualityComparer<List<(byte[] Text, long References)>>
    {
        public static readonly TextEntryComparer Instance = new();

        public bool Equals(List<(byte[] Text, long References)>? x, List<(byte[] Text, long References)>? y) =>
            x is not null && y is not null && Normal(x).SequenceEqual(Normal(y));

        public int GetHashCode(List<(byte[] Text, long References)> obj) => obj.Count;

        private static IEnumerable<string> Normal(List<(byte[] Text, long References)> entries) =>
            entries.Select(entry => $"{Convert.ToHexString(entry.Text)}:{entry.References}").Order(StringComparer.Ordinal);
    }
}
