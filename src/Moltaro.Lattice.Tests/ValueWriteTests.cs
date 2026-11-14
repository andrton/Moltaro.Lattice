namespace Moltaro.Lattice.Tests;

// Storing owned values, Lists, Maps, Sets and references is not implemented yet: until it is,
// every write of an entity type that declares a field of one of these types throws
// NotSupportedException, through records and through classes. Compiling, saving and loading
// such a schema work, and so do records and the binding of classes. An entity type of the same
// schema that declares none of these fields, also one that others refer to, is written as
// before.
public sealed class ValueWriteTests
{
    [Theory(Skip = NotImplemented.SchemaValues)]
    [InlineData("Resident")]
    [InlineData("Roster")]
    [InlineData("Settings")]
    [InlineData("Badges")]
    [InlineData("Membership")]
    public async Task Entity_type_with_a_nested_value_or_a_reference_is_not_writable_yet_through_records(string entityTypeName)
    {
        await using var database = await TestData.OpenMasterAsync(schema: SavedAndLoaded());
        var set = database.Set(entityTypeName);
        var record = CompleteRecord(set.EntityType, database.Schema);

        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.InsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.UpsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.ReplaceAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.DeleteAsync(1L));
        Assert.Empty(set.Enumerate());
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Entity_type_with_a_nested_value_or_a_reference_is_not_writable_yet_through_classes()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SavedAndLoaded());

        await ExpectNotWritableAsync(database.Set<Resident>(), new Resident { Id = 1, Home = new Address { City = "Oslo" } });
        await ExpectNotWritableAsync(database.Set<Roster>(), new Roster { Id = 1, Names = ["Ada"] });
        await ExpectNotWritableAsync(database.Set<Settings>(), new Settings { Id = 1, Values = new() { ["theme"] = "dark" } });
        await ExpectNotWritableAsync(database.Set<Badges>(), new Badges { Id = 1, Codes = [7] });
        await ExpectNotWritableAsync(database.Set<Membership>(), new Membership { Id = 1, Club = 1 });
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Entity_type_without_such_fields_is_written_as_before_in_the_same_schema()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SavedAndLoaded());

        // Membership refers to Club; Club itself declares only scalar fields.
        await database.Set<Club>().InsertAsync(new Club { Id = 1, Title = "Chess" });
        Assert.Equal("Chess", database.Set<Club>().Get(1L)!.Title);
        Assert.Equal("Chess", database.Set("Club").Get(1L)!["Title"]);
    }

    private static async Task ExpectNotWritableAsync<TEntity>(ILatticeSet<TEntity> set, TEntity entity)
        where TEntity : class
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.InsertAsync(entity));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.UpsertAsync(entity));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.ReplaceAsync(entity));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.DeleteAsync(1L));
        Assert.Empty(set.Enumerate());
    }

    // A record with every field set to a value its declaration accepts.
    private static LatticeRecord CompleteRecord(LatticeEntityType entityType, LatticeSchema schema)
    {
        Assert.True(schema.TryGetOwnedType("Address", out var address));

        var record = new LatticeRecord(entityType) { ["Id"] = 1L };
        switch (entityType.Name)
        {
            case "Resident":
                record["Home"] = new LatticeOwnedValue(address) { ["City"] = "Oslo" };
                break;
            case "Roster":
                record["Names"] = new[] { "Ada" };
                break;
            case "Settings":
                record["Values"] = new Dictionary<string, string> { ["theme"] = "dark" };
                break;
            case "Badges":
                record["Codes"] = new[] { 7 };
                break;
            case "Membership":
                record["Club"] = 1L;
                break;
        }

        return record;
    }

    // A schema with one entity type for each new type of field and one without any, compiled,
    // saved and loaded again.
    private static LatticeSchema SavedAndLoaded()
    {
        var compiled = LatticeSchema.Compile($$"""
            {
              "OwnedTypes": [
                { "Id": 1, "Name": "Address", "ClrType": "{{typeof(Address).FullName}}", "Fields": [ { "Id": 1, "Name": "City", "Type": "String" } ] }
              ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Resident", "ClrType": "{{typeof(Resident).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address" } ],
                  "PrimaryKey": [1]
                },
                {
                  "Id": 2, "Name": "Roster", "ClrType": "{{typeof(Roster).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Names", "Type": "List", "Element": { "Type": "String" } } ],
                  "PrimaryKey": [1]
                },
                {
                  "Id": 3, "Name": "Settings", "ClrType": "{{typeof(Settings).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Values", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "String" } } ],
                  "PrimaryKey": [1]
                },
                {
                  "Id": 4, "Name": "Badges", "ClrType": "{{typeof(Badges).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Codes", "Type": "Set", "Element": { "Type": "Int32" } } ],
                  "PrimaryKey": [1]
                },
                {
                  "Id": 5, "Name": "Membership", "ClrType": "{{typeof(Membership).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Club", "Type": "Reference", "EntityType": "Club" } ],
                  "PrimaryKey": [1]
                },
                {
                  "Id": 6, "Name": "Club", "ClrType": "{{typeof(Club).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Title", "Type": "String" } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);

        using var stream = new MemoryStream();
        compiled.Save(stream);
        stream.Position = 0;
        return LatticeSchema.Load(stream);
    }

    private sealed class Address
    {
        public string City { get; set; } = "";
    }

    private sealed class Resident
    {
        public long Id { get; set; }

        public Address Home { get; set; } = new();
    }

    private sealed class Roster
    {
        public long Id { get; set; }

        public List<string> Names { get; set; } = [];
    }

    private sealed class Settings
    {
        public long Id { get; set; }

        public Dictionary<string, string> Values { get; set; } = [];
    }

    private sealed class Badges
    {
        public long Id { get; set; }

        public HashSet<int> Codes { get; set; } = [];
    }

    private sealed class Membership
    {
        public long Id { get; set; }

        public long Club { get; set; }
    }

    private sealed class Club
    {
        public long Id { get; set; }

        public string Title { get; set; } = "";
    }
}
