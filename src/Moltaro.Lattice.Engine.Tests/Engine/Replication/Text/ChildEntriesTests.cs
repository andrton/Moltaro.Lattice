using Moltaro.Lattice.Engine.Tests.Engine.Text;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Text;

// A child stores each distinct text once, through its own table (brief of Epic 1, sprint S13, Acceptance:
// "a child loads shared text once and maps it to its own handles", restated by the planner's decision after
// the study, points 8 and 9: once in its memory; and the block common to S13 to S15: nothing derived
// travels, a child builds its text table itself, under its lock of the writes, with its own seed, from what
// it receives):
// - a child that loads 1,000 roots of 16 texts holds 16 entries, by its own counter, with a reference for
//   each field, and its arena holds each text once beside the roots;
// - after the changes of the stream — inserts of the same texts, a replace that keeps its text, a replace
//   to a new text, the insert and delete of the one referrer of a text — it still holds each text once;
// - a child opened with another seed of its hashes than its master holds the same entries;
// - a write through a child is stored on the master with the master's entries, and on the child with the
//   child's.
// Each count is compared with the independent tally of the test (TextTally, EntryCounts), on the child and
// on the master alike, after a drain on each. A test waits for a child through the position of a write.
public sealed class ChildEntriesTests
{
    private const ulong MasterSeed = 0x9E3779B97F4A7C15;

    private const ulong ChildSeed = 0xD1B54A32D192ED03;

    // Sixteen texts of nine characters.
    private static readonly string[] Sixteen = [.. Enumerable.Range(0, 16).Select(index => $"region {index:D2}")];

    public static IEnumerable<TheoryDataRow<string>> Encodings => TextSchema.Encodings;

    [Theory(Skip = SkipReason.BeforeSprint13)]
    [MemberData(nameof(Encodings))]
    public async Task Child_that_loads_values_of_sixteen_texts_holds_sixteen_entries_of_its_own_and_each_text_once(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Of(encoding));
        var tally = new TextTally(encoding);

        await InsertAsync(master, tally, from: 0, count: 1_000);

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        tally.ExpectHeldBy(child);
        EntryCounts.ExpectEqual(tally, child);
        Assert.Equal(16, child.Counters.TextEntries);
        EntryCounts.ExpectEqual(tally, master);
    }

    [Theory(Skip = SkipReason.BeforeSprint13)]
    [MemberData(nameof(Encodings))]
    public async Task Child_holds_each_text_once_after_the_changes_of_the_stream(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Of(encoding));
        var places = master.Set<TextSchema.Place>();
        var tally = new TextTally(encoding);

        await InsertAsync(master, tally, from: 0, count: 1_000);

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        await InsertAsync(master, tally, from: 1_000, count: 1_000);

        var kept = new TextSchema.Place { Id = 1, Number = 1, Region = Sixteen[1], Zone = Sixteen[8], Label = "kept" };
        var renamed = new TextSchema.Place { Id = 2, Number = 1, Region = "a new region", Zone = Sixteen[9], Label = "label" };

        await places.ReplaceAsync(kept);
        await places.ReplaceAsync(renamed);
        await places.InsertAsync(new TextSchema.Place { Id = 2_000, Region = "lonely", Label = "label" });
        var last = await places.DeleteAsync(2_000L);
        tally.Written(kept);
        tally.Written(renamed);

        await child.WaitForPositionAsync(last.Position);

        tally.ExpectHeldBy(child);
        EntryCounts.ExpectEqual(tally, child);
        Assert.Equal(17, child.Counters.TextEntries);
        tally.ExpectHeldBy(master);
        EntryCounts.ExpectEqual(tally, master);
    }

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public async Task Child_opened_with_another_seed_of_its_hashes_than_its_master_holds_the_same_entries()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8, new EngineTestInputs { IndexSeed = MasterSeed });
        var tally = new TextTally("Utf8");

        await InsertAsync(master, tally, from: 0, count: 300);

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = ChildSeed });

        tally.ExpectHeldBy(child);
        EntryCounts.ExpectEqual(tally, child);

        for (var id = 0; id < 300; id++)
        {
            Assert.Equal(PlaceOf(id).Region, child.Set<TextSchema.Place>().Get((long)id)?.Region);
        }
    }

    [Theory(Skip = SkipReason.BeforeSprint13)]
    [MemberData(nameof(Encodings))]
    public async Task Write_through_a_child_is_stored_with_the_entries_of_the_master_and_of_the_child(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Of(encoding));
        var tally = new TextTally(encoding);

        await InsertAsync(master, tally, from: 0, count: 64);

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var places = child.Set<TextSchema.Place>();

        // A text both hold, one neither holds, and the same texts in a field of another entity type.
        var inserted = new TextSchema.Place { Id = 64, Region = Sixteen[3], Zone = "through the child", Label = "label" };
        var replaced = new TextSchema.Place { Id = 0, Number = 1, Region = "through the child", Zone = null, Label = "label" };
        var depot = new TextSchema.Depot { Id = 1, Area = Sixteen[3] };

        await places.InsertAsync(inserted);
        await places.ReplaceAsync(replaced);
        var last = await child.Set<TextSchema.Depot>().InsertAsync(depot);
        tally.Written(inserted);
        tally.Written(replaced);
        tally.Written(depot);

        await master.WaitForPositionAsync(last.Position);

        tally.ExpectHeldBy(master);
        EntryCounts.ExpectEqual(tally, master);
        tally.ExpectHeldBy(child);
        EntryCounts.ExpectEqual(tally, child);
    }

    // The place of the id: a Region and a Zone of the sixteen texts.
    private static TextSchema.Place PlaceOf(int id) => new() { Id = id, Region = Sixteen[id % 16], Zone = Sixteen[(id + 7) % 16], Label = "label" };

    // Inserts the places of the ids from the first on, and tells the tally.
    private static async Task InsertAsync(EngineDatabase master, TextTally tally, int from, int count)
    {
        var places = master.Set<TextSchema.Place>();

        for (var id = from; id < from + count; id++)
        {
            var place = PlaceOf(id);

            await places.InsertAsync(place);
            tally.Written(place);
        }
    }
}
