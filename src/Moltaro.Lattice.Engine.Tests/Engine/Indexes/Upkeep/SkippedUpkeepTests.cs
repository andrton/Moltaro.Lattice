using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Upkeep;

// What a replace does to an index is decided by the entry its old state contributes against the entry its
// new state contributes, and the same bytes are nothing: no node of the index is copied, none retired, and
// the index holds what it held (docs/design/ordered-index.md, "The entries of one write"). The rule is the
// equality of the bytes of the two entries, not of the bytes of the two roots. Three replaces whose every
// entry stays as it was, each in a database of its own:
// - one that changes only a field in no index;
// - one whose Plain key keeps its text while its place in the root moves: a root holds its values of 64
//   bytes or less inline, in the order of the fields (Storage.RootWriter), so a longer Label, declared
//   before Code, moves Code in the root and changes its descriptor, which the entry, a copy of the text,
//   does not hold;
// - one that gives a Login longer than 64 bytes an equal text: the new root takes the same text entry,
//   which the text table finds by its bytes, so the entry, a reference to that text entry, is the same, and
//   the references of the text count what a full upkeep would leave as well, one for the root and one for
//   the node (IndexTally).
// Each states that the write retired no node of an index (RetiredNodes), that the walk of every index
// (EngineDatabase.IndexNodes, IndexContents) is as before, and what a search finds.
public sealed class SkippedUpkeepTests
{
    [Fact]
    public async Task Replace_that_changes_only_a_field_in_no_index_copies_no_node_and_leaves_every_index_as_it_was()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var ranked = database.Set<IndexSchema.Ranked>();
        var tally = await InsertRankedAsync(ranked, "login 1");
        var walks = WalksOfRanked(database);
        var replaced = WithNote(tally.Live[1]);

        var nodes = await RetiredNodes.OfAsync(database, () => ranked.Get(2L)?.Number, () => ranked.ReplaceAsync(replaced).AsTask(), "Ranked", tally.BytesOf("login 1"));
        tally.Written(replaced);

        Assert.Equal(0, nodes);
        Assert.Equal(walks, WalksOfRanked(database));
        tally.ExpectEntriesOf(database);
        RankedSearch.ExpectFound(database, replaced, [1L]);
    }

    [Fact]
    public async Task Replace_that_moves_a_Plain_key_in_its_root_and_keeps_its_text_copies_no_node_of_its_index()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var labelled = database.Set("Labelled");

        // The premise: Label comes before Code in the order of the fields, so its length decides where Code
        // lies in the root.
        var fields = labelled.EntityType.Fields.Select(field => field.Name).ToList();

        Assert.True(fields.IndexOf("Label") < fields.IndexOf("Code"), "Label does not come before Code in the order of the fields.");

        await labelled.InsertAsync(LabelledOf(labelled, 1, "a", "code"));
        await labelled.InsertAsync(LabelledOf(labelled, 2, "b", "other"));
        var walk = IndexContents.Of(database, "Labelled", "ByCode").Walk;

        var nodes = await RetiredNodes.OfAsync(database, () => labelled.Get(2L) is null ? null : 2L, () => labelled.ReplaceAsync(LabelledOf(labelled, 1, new string('a', 40), "code")).AsTask(), "Labelled", 1, 4);

        Assert.Equal(0, nodes);
        Assert.Equal(walk, IndexContents.Of(database, "Labelled", "ByCode").Walk);
        Assert.Equal([1L], labelled.Index("ByCode").Find(IndexKey.Equal("code")).Select(found => found.Get<long>("Id")));
        Assert.Equal(new string('a', 40), labelled.Get(1L)?["Label"]);
    }

    [Fact]
    public async Task Replace_that_gives_a_long_shared_key_an_equal_text_keeps_the_same_entry_and_the_references_a_full_upkeep_leaves()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var ranked = database.Set<IndexSchema.Ranked>();
        var tally = await InsertRankedAsync(ranked, new string('h', 100));
        var walks = WalksOfRanked(database);

        // A new string with the same text, so that nothing is shared before the text table finds it.
        var replaced = WithNote(tally.Live[1]);
        replaced.Login = new string('h', 100);

        var nodes = await RetiredNodes.OfAsync(database, () => ranked.Get(2L)?.Number, () => ranked.ReplaceAsync(replaced).AsTask(), "Ranked");
        tally.Written(replaced);

        Assert.Equal(0, nodes);
        Assert.Equal(walks, WalksOfRanked(database));
        tally.ExpectEntriesOf(database);
        tally.ExpectReferencesOf(database);
        RankedSearch.ExpectFound(database, replaced, [1L]);
    }

    // Three entities of Ranked, the first with the Login given, and the tally of them.
    private static async Task<IndexTally> InsertRankedAsync(ILatticeSet<IndexSchema.Ranked> ranked, string firstLogin)
    {
        var tally = new IndexTally("Utf8");

        foreach (var entity in new[]
        {
            new IndexSchema.Ranked { Id = 1, Number = 1, Login = firstLogin, Region = "North" },
            new IndexSchema.Ranked { Id = 2, Number = 2, Login = "login 2", Region = "South" },
            new IndexSchema.Ranked { Id = 3, Number = 3, Login = "login 3", Region = "East" },
        })
        {
            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        return tally;
    }

    // The state given with a Note, a field in no index.
    private static IndexSchema.Ranked WithNote(IndexSchema.Ranked state) =>
        new() { Id = state.Id, Number = state.Number, Login = state.Login, Region = state.Region, Note = "changed" };

    private static List<string> WalksOfRanked(EngineDatabase database) =>
        [.. new[] { "ByNumber", "ByLogin", "ByRegion" }.SelectMany(index => IndexContents.Of(database, "Ranked", index).Walk)];

    private static LatticeRecord LabelledOf(ILatticeSet labelled, long id, string label, string code) =>
        new(labelled.EntityType) { ["Id"] = id, ["Label"] = label, ["Code"] = code };
}
