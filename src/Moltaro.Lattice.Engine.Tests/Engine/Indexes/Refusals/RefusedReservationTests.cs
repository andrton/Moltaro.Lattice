using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;

// Index nodes are memory of the arena, counted in the budget of the instance, and a reservation of them
// that the arena cannot hold takes nothing and is refused as a write the memory budget cannot hold is:
// before its commit (docs/design/ordered-index.md, "The reservation"; decision on write admission, Q14:
// WriteRejected with Limit Memory, and the write has had no effect).
//
// The arena of these tests holds all it may beside one entity of Ranked: its fill ends by a refusal of a
// filler and leaves no free slot of the class of the nodes (Refusals.FullArena). A write of Ranked then
// finds a place for its root and for its long Login, and none for the nodes its indexes need: it is
// WriteRejected with Limit Memory and leaves nothing behind — no root entry, no prepared, live or retired
// byte, no native segment, no text entry and no reference, nothing found by a search; every counter is as
// before — and the instance stays Ready. One thing it may leave grown: the managed capacity of the text
// table, whose bucket table a new entry may have grown before the arena refused the entry, and which keeps
// that room for the next one (Storage.Text.TextTable). That capacity is reported and not charged to the
// memory budget (EngineCounters, TextTableBytes); it never shrinks for the refusal. Once memory is freed,
// the same write succeeds and is found.
public sealed class RefusedReservationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Write_whose_index_nodes_the_arena_cannot_hold_is_refused_for_memory_leaves_nothing_and_succeeds_once_memory_is_freed()
    {
        await using var database = await FullArena.OpenAsync();
        var ranked = database.Set<IndexSchema.Ranked>();

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
        await FullArena.FreeAsync(database, 1, 10);

        await ranked.InsertAsync(new IndexSchema.Ranked { Id = 2, Number = 2, Login = new string('b', 100), Region = "North" });

        Assert.Equal(2, Assert.Single(ranked.Index("ByNumber").Find(IndexKey.Equal(2L))).Id);
        Assert.Equal(2, Assert.Single(ranked.Index("ByLogin").Find(IndexKey.Equal(new string('b', 100)))).Id);
        Assert.Equal([1L, 2L], ranked.Index("ByRegion").Find(IndexKey.Equal("NORTH")).Select(entity => entity.Id).Order());
    }

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
