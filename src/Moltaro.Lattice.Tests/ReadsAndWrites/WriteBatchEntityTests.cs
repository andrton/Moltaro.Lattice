namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// A write batch takes one operation per entity, and it tells entities apart as the schema reads the
// key, not as the caller spelled it (the planner's answer to Q45; the remarks of ILatticeWriteBatch):
// two operations address one entity when they name the same entity type and their keys are equal
// after the key is brought to the declared key type and compared under the comparison the schema
// declares for the key field. A second operation for one entity is an ArgumentException when it is
// added, by class or by record, as an insert, a replace, an upsert or a delete by key, and the batch
// stays as it was: its count, the entity it refuses, and the other entities it still takes. Keys that
// the declared comparison holds different are two entities.
//
// The check lives in the batch of the core, which is built for any schema. Account has a
// String key compared without case, Note one compared with Ordinal, and Counter an Int32 key.
public sealed class WriteBatchEntityTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Account", "ClrType": "{{typeof(Account).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Login", "Type": "String", "Comparison": "OrdinalIgnoreCase" }, { "Id": 2, "Name": "Name", "Type": "String" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Title", "Type": "String" }, { "Id": 2, "Name": "Body", "Type": "String" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "Counter", "ClrType": "{{typeof(Counter).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int32" }, { "Id": 2, "Name": "Value", "Type": "Int64" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact]
    public async Task Two_spellings_of_a_key_compared_without_case_are_one_entity()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var accounts = database.Set("Account");
        var batch = database.CreateWriteBatch();

        // An insert by class, then a delete by key, an operation by record and a delete by name under
        // other spellings; outside ASCII, an upsert by record, then a replace by class.
        batch.Insert(new Account { Login = "Main", Name = "first" });
        Assert.Throws<ArgumentException>(() => batch.Delete<Account>("MAIN"));
        Assert.Throws<ArgumentException>(() => batch.Upsert(new LatticeRecord(accounts.EntityType) { ["Login"] = "main", ["Name"] = "second" }));
        Assert.Throws<ArgumentException>(() => batch.Delete("Account", "mAIN"));

        batch.Upsert(new LatticeRecord(accounts.EntityType) { ["Login"] = "Été", ["Name"] = "first" });
        Assert.Throws<ArgumentException>(() => batch.Replace(new Account { Login = "ÉTÉ", Name = "second" }));

        // The batch is as it was: two operations, the entities still refused, another one taken.
        Assert.Equal(2, batch.Count);
        Assert.Throws<ArgumentException>(() => batch.Delete<Account>("main"));
        Assert.Throws<ArgumentException>(() => batch.Delete("Account", "été"));
        batch.Delete("Account", "Mains");
        Assert.Equal(3, batch.Count);
    }

    [Fact]
    public async Task Integer_key_given_in_two_widths_is_one_entity()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var counters = database.Set("Counter");
        var batch = database.CreateWriteBatch();

        // An insert by class with the Int32 key, then deletes by key with an Int64 and a UInt8, and an
        // operation by record with an Int16.
        batch.Insert(new Counter { Id = 5, Value = 1 });
        Assert.Throws<ArgumentException>(() => batch.Delete("Counter", 5L));
        Assert.Throws<ArgumentException>(() => batch.Delete<Counter>((byte)5));
        Assert.Throws<ArgumentException>(() => batch.Upsert(new LatticeRecord(counters.EntityType) { ["Id"] = (short)5, ["Value"] = 2L }));

        // The batch is as it was: one operation, the entity still refused, another one taken.
        Assert.Equal(1, batch.Count);
        Assert.Throws<ArgumentException>(() => batch.Replace(new Counter { Id = 5, Value = 3 }));
        batch.Delete("Counter", 6L);
        Assert.Equal(2, batch.Count);
    }

    [Fact]
    public async Task Two_spellings_of_a_key_compared_with_Ordinal_are_two_entities()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var notes = database.Set("Note");
        var batch = database.CreateWriteBatch();

        batch.Insert(new Note { Title = "Main", Body = "first" });
        batch.Delete<Note>("MAIN");
        batch.Upsert(new LatticeRecord(notes.EntityType) { ["Title"] = "main", ["Body"] = "third" });

        // The spelling given first is still one entity.
        Assert.Throws<ArgumentException>(() => batch.Delete("Note", "Main"));
        Assert.Equal(3, batch.Count);
    }

    private sealed class Account
    {
        public string Login { get; set; } = "";

        public string Name { get; set; } = "";
    }

    private sealed class Note
    {
        public string Title { get; set; } = "";

        public string Body { get; set; } = "";
    }

    private sealed class Counter
    {
        public int Id { get; set; }

        public long Value { get; set; }
    }
}
