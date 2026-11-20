using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Upkeep;

// A replace that gives an entity another key in an index inserts its new entry and removes the old one,
// each by a copy of the leaf that holds it (docs/design/ordered-index.md, "The entries of one write"):
// - a replace that changes one key of Ranked and keeps the others: the entry moves in ByNumber, and the
//   other indexes hold the entries of the live entities as before (IndexTally);
// - a replace that swaps two parts of a composite key of Tripled: the same values in another order are
//   another key, and the entry moves to it;
// - two replaces that swap the keys of two entities of Counted, one after the other: after the first both
//   entities have the one key, after the second each has the other's.
// Each states that a write retired a node (RetiredNodes), the entries in the walk of the index
// (EngineDatabase.IndexNodes, IndexContents) and what a search by the old and the new key finds.
public sealed class MovedEntryTests
{
    [Fact]
    public async Task Replace_that_changes_one_key_moves_its_entry_and_leaves_the_entries_of_the_other_indexes()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var ranked = database.Set<IndexSchema.Ranked>();
        var tally = new IndexTally("Utf8");

        for (var id = 1L; id <= 3; id++)
        {
            var entity = new IndexSchema.Ranked { Id = id, Number = id, Login = $"login {id}", Region = "North" };

            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        var moved = new IndexSchema.Ranked { Id = 1, Number = 5, Login = "login 1", Region = "North" };

        var nodes = await RetiredNodes.OfAsync(database, () => ranked.Get(2L)?.Number, () => ranked.ReplaceAsync(moved).AsTask(), "Ranked", tally.BytesOf("login 1"));
        tally.Written(moved);

        Assert.True(nodes > 0, "The replace retired no node of ByNumber.");
        Assert.Equal([(2L, 2L), (3L, 3L), (5L, 1L)], IndexContents.Of(database, "Ranked", "ByNumber").Entries.Select(entry => ((long)entry.Key[0]!, entry.Entity)));
        tally.ExpectEntriesOf(database);
        Assert.Empty(ranked.Index("ByNumber").Find(IndexKey.Equal(1L)));
        Assert.Equal([1L], ranked.Index("ByNumber").Find(IndexKey.Equal(5L)).Select(entity => entity.Id));
        Assert.Equal([1L], ranked.Index("ByLogin").Find(IndexKey.Equal("login 1")).Select(entity => entity.Id));
    }

    [Fact]
    public async Task Replace_that_swaps_two_parts_of_a_composite_key_moves_its_entry_to_the_other_order()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var tripled = database.Set<IndexSchema.Tripled>();

        await tripled.InsertAsync(new IndexSchema.Tripled { Id = 1, First = "x", Second = "y", Third = "z" });
        await tripled.InsertAsync(new IndexSchema.Tripled { Id = 2, First = "a", Second = "b", Third = "c" });

        var nodes = await RetiredNodes.OfAsync(database, () => tripled.Get(2L)?.Id, () => tripled.ReplaceAsync(new IndexSchema.Tripled { Id = 1, First = "y", Second = "x", Third = "z" }).AsTask(), "Tripled", 1, 1, 1);

        Assert.True(nodes > 0, "The replace retired no node of ByAll.");
        Assert.Equal(["a b c 2", "y x z 1"], IndexContents.Of(database, "Tripled", "ByAll").Entries.Select(entry => $"{string.Join(" ", entry.Key)} {entry.Entity}"));
        Assert.Empty(tripled.Index("ByAll").Find(IndexKey.Equal("x", "y", "z")));
        Assert.Equal([1L], tripled.Index("ByAll").Find(IndexKey.Equal("y", "x", "z")).Select(entity => entity.Id));
    }

    [Fact]
    public async Task Replaces_that_swap_the_keys_of_two_entities_in_two_writes_leave_each_entity_under_the_key_of_the_other()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var counted = database.Set<IndexSchema.Counted>();

        await counted.InsertAsync(new IndexSchema.Counted { Id = 1, Number = 10 });
        await counted.InsertAsync(new IndexSchema.Counted { Id = 2, Number = 20 });

        var first = await RetiredNodes.OfAsync(database, () => counted.Get(1L)?.Number, () => counted.ReplaceAsync(new IndexSchema.Counted { Id = 1, Number = 20 }).AsTask(), "Counted");

        Assert.True(first > 0, "The first replace retired no node of ByNumber.");
        Assert.Equal([(20L, 1L), (20L, 2L)], EntriesOf(database));
        Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(10L)));
        Assert.Equal([1L, 2L], counted.Index("ByNumber").Find(IndexKey.Equal(20L)).Select(entity => entity.Id).Order());

        var second = await RetiredNodes.OfAsync(database, () => counted.Get(1L)?.Number, () => counted.ReplaceAsync(new IndexSchema.Counted { Id = 2, Number = 10 }).AsTask(), "Counted");

        Assert.True(second > 0, "The second replace retired no node of ByNumber.");
        Assert.Equal([(10L, 2L), (20L, 1L)], EntriesOf(database));
        Assert.Equal([2L], counted.Index("ByNumber").Find(IndexKey.Equal(10L)).Select(entity => entity.Id));
        Assert.Equal([1L], counted.Index("ByNumber").Find(IndexKey.Equal(20L)).Select(entity => entity.Id));
    }

    // The entries of ByNumber of Counted, each its key and its entity, in the order of the walk.
    private static List<(long Key, long Entity)> EntriesOf(EngineDatabase database) =>
        [.. IndexContents.Of(database, "Counted", "ByNumber").Entries.Select(entry => ((long)entry.Key[0]!, entry.Entity))];
}
