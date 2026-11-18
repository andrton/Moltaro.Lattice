using static Moltaro.Lattice.Tests.DictionaryStrings.DictionarySchema;

namespace Moltaro.Lattice.Tests.DictionaryStrings;

// The text of a field with Dictionary storage reads back exactly as it was written (decision on strings,
// Q04.2: "Dictionary entries must preserve the original text"; brief of Epic 1, sprint S13, Acceptance):
// by key and in an enumeration, through the class and by name, after an insert, a replace and an upsert;
// after a delete the entity is absent and the others keep their text. Equal text is stored once for the
// database and shared (S13), which a reader cannot see: a write of one entity changes the text of no
// other. The comparison of a field applies only where text is compared, so on a field compared with
// OrdinalIgnoreCase as with Ordinal the exact case written comes back, and two entities whose texts
// differ only in case each read back their own spelling, whichever was written first. All of it in a
// UTF-8 and in a UTF-16 database.
public sealed class DictionaryTextTests
{
    // Text of every width of a character in both encodings: ASCII, Latin, Cyrillic, CJK and a character
    // outside the basic plane, which UTF-16 stores as a pair of surrogates.
    private static readonly string[] Texts = ["North", "Südwest", "Жук", "日本", "𐐀𐐩", "a😀b"];

    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum, LatticeStringComparisonEnum>> Databases => EveryDatabase;

    [Theory]
    [MemberData(nameof(Databases))]
    public async Task Text_reads_back_as_written_by_key_through_the_class_and_by_name(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison)
    {
        await using var database = await OpenAsync(encoding, comparison);
        var places = database.Set<Place>();
        var records = database.Set("Place");

        for (var id = 0; id < Texts.Length; id++)
        {
            var zone = Texts[^(id + 1)];

            if (id % 2 == 0)
            {
                await places.InsertAsync(new Place { Id = id, Region = Texts[id], Zone = zone });
            }
            else
            {
                await records.InsertAsync(PlaceRecord(records, id, Texts[id], zone));
            }
        }

        for (var id = 0; id < Texts.Length; id++)
        {
            ExpectText(places, records, id, Texts[id], Texts[^(id + 1)]);

            var read = await places.GetAsync(id, new LatticeSession());

            Assert.True(read.Found);
            Assert.Equal(Texts[id], read.Entity!.Region);
        }
    }

    [Theory]
    [MemberData(nameof(Databases))]
    public async Task Text_reads_back_as_written_in_an_enumeration_through_the_class_and_by_name(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison)
    {
        await using var database = await OpenAsync(encoding, comparison);
        var places = database.Set<Place>();
        var records = database.Set("Place");

        for (var id = 0; id < Texts.Length; id++)
        {
            await places.InsertAsync(new Place { Id = id, Region = Texts[id], Zone = Texts[0] });
        }

        Assert.Equal(Texts, places.Enumerate().OrderBy(place => place.Id).Select(place => place.Region));
        Assert.Equal(Texts, records.Enumerate().OrderBy(record => record.Get<long>("Id")).Select(record => record.Get<string>("Region")));
        Assert.All(places.Enumerate(), place => Assert.Equal(Texts[0], place.Zone));

        var enumerated = await places.EnumerateAsync(new LatticeSession()).ToListAsync();

        Assert.Equal(Texts, enumerated.OrderBy(place => place.Id).Select(place => place.Region));
    }

    [Theory]
    [MemberData(nameof(Databases))]
    public async Task Replace_and_upsert_read_back_the_new_text_and_leave_the_text_of_every_other_entity(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison)
    {
        await using var database = await OpenAsync(encoding, comparison);
        var places = database.Set<Place>();
        var records = database.Set("Place");

        // Three entities share North, in both of its fields.
        for (var id = 1; id <= 3; id++)
        {
            await places.InsertAsync(new Place { Id = id, Region = "North", Zone = "North" });
        }

        await places.ReplaceAsync(new Place { Id = 1, Region = "South", Zone = "North" });
        await records.ReplaceAsync(PlaceRecord(records, 2, "Жук", zone: null));
        await records.UpsertAsync(PlaceRecord(records, 3, "North", "South"));
        await places.UpsertAsync(new Place { Id = 4, Region = "North", Zone = "South" });

        ExpectText(places, records, 1, "South", "North");
        ExpectText(places, records, 2, "Жук", null);
        ExpectText(places, records, 3, "North", "South");
        ExpectText(places, records, 4, "North", "South");

        // A replace back to text the entity held before, and one that keeps its text.
        await places.ReplaceAsync(new Place { Id = 1, Region = "North", Zone = "North", Label = "back" });
        await places.ReplaceAsync(new Place { Id = 4, Region = "North", Zone = "South", Label = "kept" });

        ExpectText(places, records, 1, "North", "North");
        ExpectText(places, records, 4, "North", "South");
        Assert.Equal("kept", places.Get(4)!.Label);
    }

    [Theory]
    [MemberData(nameof(Databases))]
    public async Task Deleted_entity_is_absent_and_the_entities_that_shared_its_text_keep_it(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison)
    {
        await using var database = await OpenAsync(encoding, comparison);
        var places = database.Set<Place>();
        var records = database.Set("Place");

        await places.InsertAsync(new Place { Id = 1, Region = "North", Zone = "Жук" });
        await places.InsertAsync(new Place { Id = 2, Region = "North", Zone = "Жук" });
        await places.InsertAsync(new Place { Id = 3, Region = "a😀b" });

        Assert.True((await places.DeleteAsync(1)).Changed);
        Assert.True((await records.DeleteAsync(3L)).Changed);

        Assert.Null(places.Get(1));
        Assert.Null(records.Get(3L));
        ExpectText(places, records, 2, "North", "Жук");

        // The text of an entity deleted last is written again.
        Assert.True((await places.DeleteAsync(2)).Changed);
        await places.InsertAsync(new Place { Id = 5, Region = "North", Zone = "a😀b" });

        Assert.Equal([5L], places.Enumerate().Select(place => place.Id));
        ExpectText(places, records, 5, "North", "a😀b");
    }

    [Theory]
    [MemberData(nameof(Databases))]
    public async Task Texts_that_differ_only_in_case_each_read_back_their_own_spelling(LatticeStringEncodingEnum encoding, LatticeStringComparisonEnum comparison)
    {
        await using var database = await OpenAsync(encoding, comparison);
        var places = database.Set<Place>();
        var records = database.Set("Place");

        // Spellings that a comparison without case takes for one text, written in either order.
        string[] spellings = ["North", "NORTH", "north", "Été", "ÉTÉ", "été", "𐐀𐐩", "𐐨𐐁"];

        for (var id = 0; id < spellings.Length; id++)
        {
            await places.InsertAsync(new Place { Id = id, Region = spellings[id], Zone = spellings[^(id + 1)] });
        }

        for (var id = 0; id < spellings.Length; id++)
        {
            ExpectText(places, records, id, spellings[id], spellings[^(id + 1)]);
        }

        // A replace under another spelling stores that spelling.
        await places.ReplaceAsync(new Place { Id = 0, Region = "nORTH", Zone = "North" });

        ExpectText(places, records, 0, "nORTH", "North");
        ExpectText(places, records, 1, "NORTH", spellings[^2]);
    }

    // The entity of the id has the texts given, through the class and by name.
    private static void ExpectText(ILatticeSet<Place> places, ILatticeSet records, long id, string region, string? zone)
    {
        var place = places.Get(id);
        var record = records.Get(id);

        Assert.NotNull(place);
        Assert.Equal(region, place.Region);
        Assert.Equal(zone, place.Zone);
        Assert.NotNull(record);
        Assert.Equal(region, record["Region"]);
        Assert.Equal(zone, record["Zone"]);
        Assert.True(places.TryGet(id, out var withRevision, out _));
        Assert.Equal(region, withRevision.Region);
    }
}
