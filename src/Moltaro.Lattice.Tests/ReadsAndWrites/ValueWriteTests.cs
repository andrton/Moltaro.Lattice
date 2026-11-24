namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// Storing Maps and Sets is not implemented yet: until it is, every write of an entity type that
// declares a field of one of these types throws NotSupportedException, through records and through
// classes. Compiling, saving and loading such a schema work, and so do records and the binding of
// classes. An entity type of the same schema with an owned value, a List or a reference is written and
// read back by name, and one that declares none of these fields, also one that others refer to, is
// written as before.
public sealed class ValueWriteTests
{
    [Theory]
    [InlineData("Settings")]
    [InlineData("Badges")]
    public async Task Entity_type_with_a_Map_or_a_Set_is_not_writable_yet_through_records(string entityTypeName)
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

    [Fact]
    public async Task Entity_type_with_a_Map_or_a_Set_is_not_writable_yet_through_classes()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SavedAndLoaded());

        await ExpectNotWritableAsync(database.Set<Settings>(), new Settings { Id = 1, Values = new() { ["theme"] = "dark" } });
        await ExpectNotWritableAsync(database.Set<Badges>(), new Badges { Id = 1, Codes = [7] });
    }

    [Theory]
    [InlineData("Resident")]
    [InlineData("Roster")]
    [InlineData("Membership")]
    public async Task Entity_type_of_a_saved_and_loaded_schema_with_an_owned_value_a_List_or_a_reference_is_written_and_read_back_by_name(string entityTypeName)
    {
        await using var database = await TestData.OpenMasterAsync(schema: SavedAndLoaded());
        var set = database.Set(entityTypeName);

        await set.InsertAsync(CompleteRecord(set.EntityType, database.Schema));

        var read = set.Get(1L)!;
        var written = CompleteRecord(set.EntityType, database.Schema);

        Assert.All(set.EntityType.Fields, field => Assert.Equal(Show(written[field.Name]), Show(read[field.Name])));
    }

    [Fact]
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

    // A value of a record, its owned values field by field.
    private static string Show(object? value) => value switch
    {
        LatticeOwnedValue owned => string.Join(", ", owned.OwnedType.Fields.Select(field => $"{field.Name} {Show(owned[field.Name])}")),
        System.Collections.IEnumerable and not string => string.Join(", ", ((System.Collections.IEnumerable)value).Cast<object?>().Select(Show)),
        _ => $"{value}",
    };

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
