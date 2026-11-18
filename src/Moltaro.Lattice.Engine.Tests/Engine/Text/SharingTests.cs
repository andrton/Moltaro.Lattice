namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Equal text of Dictionary fields is stored once for the database (brief of Epic 1, sprint S13, Goal and
// Acceptance; decision on strings, Q04.3: one dictionary per database, shared by every field that selects
// Dictionary, also across entity types; the planner's decision after the study, points 2 to 4):
// - values of sixteen texts have sixteen entries, and the arena holds each text once, as one block of
//   the stored bytes beside the roots, which hold only their descriptors;
// - equal text in two fields of one entity type and in a field of another entity type is one entry with
//   a reference for each field that holds it;
// - an entry is keyed by its exact bytes: texts that differ only in case are two entries, also on a field
//   compared without case;
// - the empty text and null take no entry, and Plain text takes none and shares none;
// - a replace that keeps its text keeps the count of the text;
// - the managed bytes of the text table are reported (point 7).
// Every count is compared with the independent tally of the test (TextTally, EntryCounts), after the
// writes have returned and a drain has run.
public sealed class SharingTests
{
    // Sixteen texts of nine characters.
    private static readonly string[] Sixteen = [.. Enumerable.Range(0, 16).Select(index => $"region {index:D2}")];

    public static IEnumerable<TheoryDataRow<string>> Encodings => TextSchema.Encodings;

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Thousand_values_of_sixteen_texts_have_sixteen_entries_and_the_arena_holds_each_text_once(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally(encoding);

        for (var id = 0; id < 1_000; id++)
        {
            var place = new TextSchema.Place { Id = id, Region = Sixteen[id % 16], Zone = Sixteen[(id + 3) % 16], Label = "label" };

            await places.InsertAsync(place);
            tally.Written(place);
        }

        Assert.Equal(16, database.Counters.TextEntries);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Equal_text_in_two_fields_and_in_another_entity_type_is_one_entry_with_a_reference_for_each_field(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var tally = new TextTally(encoding);
        var place = new TextSchema.Place { Id = 1, Region = "North", Zone = "North", Label = "label" };
        var other = new TextSchema.Place { Id = 2, Region = "South", Zone = null, Label = "label" };
        var depot = new TextSchema.Depot { Id = 1, Area = "North" };

        await database.Set<TextSchema.Place>().InsertAsync(place);
        await database.Set("Place").InsertAsync(TextSchema.RecordOf(database.Set("Place").EntityType, other));
        await database.Set<TextSchema.Depot>().InsertAsync(depot);
        tally.Written(place);
        tally.Written(other);
        tally.Written(depot);

        Assert.Equal(3, EntryCounts.Of(database, encoding)["North"]);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Texts_that_differ_only_in_case_are_two_entries_also_on_a_field_compared_without_case(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var places = database.Set<TextSchema.Place>();
        string[] spellings = ["North", "north", "NORTH"];
        var tally = new TextTally(encoding);

        // Region is compared without case, Zone with Ordinal.
        for (var id = 0; id < spellings.Length; id++)
        {
            var place = new TextSchema.Place { Id = id, Region = spellings[id], Zone = spellings[^(id + 1)], Label = "label" };

            await places.InsertAsync(place);
            tally.Written(place);
        }

        Assert.Equal(3, database.Counters.TextEntries);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);

        for (var id = 0; id < spellings.Length; id++)
        {
            Assert.Equal(spellings[id], places.Get(id)?.Region);
        }
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Empty_text_and_null_take_no_entry(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally(encoding);

        foreach (var place in new[]
        {
            new TextSchema.Place { Id = 1, Region = "", Zone = null, Label = "label" },
            new TextSchema.Place { Id = 2, Region = "", Zone = "", Label = "" },
        })
        {
            await places.InsertAsync(place);
            tally.Written(place);
        }

        Assert.Equal(0, database.Counters.TextEntries);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
        Assert.Equal(("", null), (places.Get(1L)!.Region, places.Get(1L)!.Zone));
        Assert.Equal(("", ""), (places.Get(2L)!.Region, places.Get(2L)!.Zone));
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Plain_text_equal_to_a_Dictionary_text_takes_no_entry_and_shares_none(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var places = database.Set<TextSchema.Place>();
        var long300 = new string('p', 300);
        var tally = new TextTally(encoding);

        // Label is Plain: inline, and in a block of its own.
        foreach (var place in new[]
        {
            new TextSchema.Place { Id = 1, Region = "North", Label = "North" },
            new TextSchema.Place { Id = 2, Region = long300, Label = long300 },
        })
        {
            await places.InsertAsync(place);
            tally.Written(place);
        }

        Assert.Equal(1, EntryCounts.Of(database, encoding)["North"]);
        Assert.Equal(1, EntryCounts.Of(database, encoding)[long300]);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Replace_that_keeps_its_text_keeps_the_count_of_the_text(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var places = database.Set<TextSchema.Place>();
        var records = database.Set("Place");
        var tally = new TextTally(encoding);

        foreach (var id in new[] { 1L, 2L })
        {
            var place = new TextSchema.Place { Id = id, Region = "North", Zone = "South", Label = "first" };

            await places.InsertAsync(place);
            tally.Written(place);
        }

        // Through the class, by name, and an upsert of the stored entity.
        var replaced = new TextSchema.Place { Id = 1, Number = 1, Region = "North", Zone = "South", Label = "second" };
        var byName = new TextSchema.Place { Id = 2, Number = 1, Region = "North", Zone = "South", Label = "second" };
        var upserted = new TextSchema.Place { Id = 1, Number = 2, Region = "North", Zone = "South", Label = "third" };

        await places.ReplaceAsync(replaced);
        await records.ReplaceAsync(TextSchema.RecordOf(records.EntityType, byName));
        await places.UpsertAsync(upserted);
        tally.Written(replaced);
        tally.Written(byName);
        tally.Written(upserted);

        Assert.Equal(2, EntryCounts.Of(database, encoding)["North"]);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
    }

    [Fact]
    public async Task Text_table_reports_at_least_the_address_and_the_count_of_each_entry_as_its_managed_bytes()
    {
        const int Texts = 4_096;

        await using var database = await EngineMaster.OpenAsync(TextSchema.Utf8);
        var places = database.Set<TextSchema.Place>();

        for (var id = 0; id < Texts; id++)
        {
            await places.InsertAsync(new TextSchema.Place { Id = id, Region = $"text {id}", Label = "label" });
        }

        var counters = database.Counters;

        // A slot holds the relative address of its entry and its references as an Int64, besides its hash
        // and its flag or link (point 3).
        Assert.Equal(Texts, counters.TextEntries);
        Assert.True(counters.TextTableBytes >= 16L * Texts, $"The text table reports {counters.TextTableBytes} bytes for {Texts} entries.");
    }
}
