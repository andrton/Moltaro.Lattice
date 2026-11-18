namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// By decision O19 an entity has no size limit of its own: how large an entity is, is the
// application's choice, and only the memory of the instance bounds it. An entity of 4 MiB, four
// times the starting limit the engine had before the decision, is stored and read through its class
// and by name like any other.
//
// The bound of one String or Binary value, which is not a limit of the entity, is stated by
// ValueBoundWriteTests.
public sealed class EntitySizeTests
{
    // Four mebibytes of ASCII text, one byte for every character in UTF-8.
    private const int FourMebibytes = 4 * 1024 * 1024;

    [Fact]
    public async Task Entity_of_4_MiB_is_stored_and_read_through_its_class_and_by_name()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema());
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
