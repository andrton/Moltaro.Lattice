using static Moltaro.Lattice.Tests.DictionaryStrings.DictionarySchema;

namespace Moltaro.Lattice.Tests.DictionaryStrings;

// Text that is not well-formed, a surrogate without its pair, is refused in a field with Dictionary
// storage as in a Plain field (the planner's answer to Q41; MalformedTextWriteTests and
// MalformedTextKeyTests of ReadsAndWrites): ValidationFailed with the field named, through the class and
// by name, also as the value of a Dictionary key, and when it is added to a batch. The validation of the
// core makes the check before the engine sees the write, so the first test holds before the engine
// stores Dictionary text as well. Such text is never stored: a stored entity keeps its state and its
// revision, and a surrogate with its pair, in its order, is text like any other.
public sealed class MalformedDictionaryTextTests
{
    private const string Bad = "No\uD83Drth";

    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum>> Encodings => EveryEncoding;

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Text_that_is_not_well_formed_in_a_Dictionary_field_is_ValidationFailed_with_the_field_named(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var places = database.Set<Place>();
        var records = database.Set("Place");
        var codes = database.Set<Code>();

        await TestData.ExpectValidationFailedAsync("Region", () => places.InsertAsync(new Place { Id = 1, Region = Bad }));
        await TestData.ExpectValidationFailedAsync("Zone", () => places.UpsertAsync(new Place { Id = 1, Region = "North", Zone = Bad }));
        await TestData.ExpectValidationFailedAsync("Region", () => places.ReplaceAsync(new Place { Id = 1, Region = Bad }));
        await TestData.ExpectValidationFailedAsync("Zone", () => records.InsertAsync(PlaceRecord(records, 1, "North", Bad)));
        await TestData.ExpectValidationFailedAsync("Region", () => records.UpsertAsync(PlaceRecord(records, 1, Bad)));
        await TestData.ExpectValidationFailedAsync("Name", () => codes.InsertAsync(new Code { Name = Bad, Body = "North" }));
        await TestData.ExpectValidationFailedAsync("Body", () => codes.UpsertAsync(new Code { Name = "North", Body = Bad }));

        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Region", () => batch.Insert(new Place { Id = 1, Region = Bad }));
        TestData.ExpectValidationFailed("Zone", () => batch.Upsert(PlaceRecord(records, 1, "North", Bad)));
        Assert.Equal(0, batch.Count);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Stored_entity_keeps_its_state_when_a_write_of_it_with_text_that_is_not_well_formed_is_refused(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var places = database.Set<Place>();
        var records = database.Set("Place");
        await places.InsertAsync(new Place { Id = 1, Region = "North", Zone = "South" });
        Assert.True(places.TryGet(1, out _, out var revision));

        await TestData.ExpectValidationFailedAsync("Region", () => places.ReplaceAsync(new Place { Id = 1, Region = Bad, Zone = "South" }));
        await TestData.ExpectValidationFailedAsync("Zone", () => records.UpsertAsync(PlaceRecord(records, 1, "North", Bad)));
        await TestData.ExpectValidationFailedAsync("Region", () => places.ReplaceAsync(new Place { Id = 1, Region = Bad }, revision));

        Assert.True(places.TryGet(1, out var stored, out var after));
        Assert.Equal(("North", "South"), (stored.Region, stored.Zone));
        Assert.Equal(revision, after);
        Assert.Single(places.Enumerate());
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Surrogate_pair_in_its_order_is_stored_in_a_Dictionary_field_and_reads_back(LatticeStringEncodingEnum encoding)
    {
        await using var database = await OpenAsync(encoding);
        var places = database.Set<Place>();
        var codes = database.Set<Code>();

        await places.InsertAsync(new Place { Id = 1, Region = "No😀rth", Zone = "😀" });
        await codes.InsertAsync(new Code { Name = "😀", Body = "No😀rth" });

        Assert.Equal(("No😀rth", "😀"), (places.Get(1)!.Region, places.Get(1)!.Zone));
        Assert.Equal("No😀rth", codes.Get("😀")?.Body);
    }
}
