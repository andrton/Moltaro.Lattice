namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// By decision O19 an entity has no size limit of its own: how large an entity is, is the
// application's choice, and only the memory of the instance bounds it. An entity of 4 MiB, four
// times the starting limit the engine had before the decision, is stored and read through its class
// and by name like any other, on both implementations.
//
// One String or Binary value takes at most 268,173,376 bytes of its stored encoding (the remarks of
// String and Binary of LatticeFieldTypeEnum): a write of an entity with a larger value is
// ValidationFailed with the field named, through the class and by name, and stores nothing. The
// engine checks that bound, and the placeholder gets no stand-in check (brief of Epic 1, sprint
// S06, the coordinator's note on the bound), so that test runs on the engine only. Its text takes
// 179 MB: 89,391,125 times €, three bytes each in UTF-8, and two a, 268,173,377 bytes. A Binary
// value above the bound is stated by the tests of the root writer: through the public API both its
// class and its record would copy an array of 268 MB.
public sealed class EntitySizeTests
{
    // Four mebibytes of ASCII text, one byte for every character in UTF-8.
    private const int FourMebibytes = 4 * 1024 * 1024;

    private const int Euros = 89_391_125;

    private static readonly Lazy<string> TextAboveTheBound = new(() => string.Create(Euros + 2, 0, static (characters, _) =>
    {
        characters[..Euros].Fill('€');
        characters[Euros..].Fill('a');
    }));

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Entity_of_4_MiB_is_stored_and_read_through_its_class_and_by_name(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, Schema());
        var notes = database.Set<Note>();
        var records = database.Set("Note");
        var first = new string('f', FourMebibytes);
        var second = new string('s', FourMebibytes);

        await notes.InsertAsync(new Note { Id = 1, Body = first });
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 2L, ["Body"] = second });

        Assert.Equal(first, notes.Get(1L)!.Body);
        Assert.Equal(first, records.Get(1L)!["Body"]);
        Assert.Equal(second, notes.Get(2L)!.Body);
        Assert.Equal(second, records.Get(2L)!["Body"]);

        // Replaced by one of the same size, through the class and by name.
        await notes.ReplaceAsync(new Note { Id = 2, Body = first });
        await records.UpsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Body"] = second });

        Assert.Equal(second, notes.Get(1L)!.Body);
        Assert.Equal(first, records.Get(2L)!["Body"]);
    }

    [Fact]
    public async Task Write_of_a_value_whose_encoding_is_above_the_bound_of_one_value_is_ValidationFailed_with_the_field_named()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, Schema());
        var notes = database.Set<Note>();
        var records = database.Set("Note");
        await notes.InsertAsync(new Note { Id = 1, Body = "kept" });
        var text = TextAboveTheBound.Value;
        LatticeRecord Record(long id) => new(records.EntityType) { ["Id"] = id, ["Body"] = text };

        // As a new entity and as a new state of one that exists.
        await ExpectRefusedAsync(() => notes.InsertAsync(new Note { Id = 2, Body = text }));
        await ExpectRefusedAsync(() => notes.UpsertAsync(new Note { Id = 2, Body = text }));
        await ExpectRefusedAsync(() => notes.ReplaceAsync(new Note { Id = 1, Body = text }));
        await ExpectRefusedAsync(() => records.InsertAsync(Record(2)));
        await ExpectRefusedAsync(() => records.UpsertAsync(Record(1)));
        await ExpectRefusedAsync(() => records.ReplaceAsync(Record(1)));

        Assert.Null(notes.Get(2L));
        Assert.Equal("kept", notes.Get(1L)!.Body);

        static async Task ExpectRefusedAsync(Func<ValueTask<LatticeWriteResult>> write)
        {
            var exception = await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, write);

            Assert.Contains("Body", exception.Message, StringComparison.Ordinal);
        }
    }

    // The entity type Note, bound to the class Note.
    private static LatticeSchema Schema() => LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Body", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Note
    {
        public long Id { get; set; }

        public string Body { get; set; } = "";
    }
}
