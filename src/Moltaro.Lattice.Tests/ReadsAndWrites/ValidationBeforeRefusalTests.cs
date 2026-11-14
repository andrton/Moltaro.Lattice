namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// A write first checks what needs no engine: its arguments, and the entity against the schema
// at every depth. Only a write that passes is refused as not implemented. That holds for every
// entity type whose writes the placeholder refuses: those with owned values, collections or
// references, and those of Epic 1, sprint 2, with a unique index, a comparison that ignores
// case, Dictionary storage, an enum field, or in a schema of the UTF-16 encoding. A check that
// belongs to what is not implemented is not made: an integer that its enum type does not
// declare is not refused yet, and a unique index is not consulted.
public sealed class ValidationBeforeRefusalTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Valid_record_or_object_with_nested_values_ends_in_NotSupportedException()
    {
        await using var database = await TestData.OpenMasterAsync(schema: NestedSchema());

        await ExpectNotSupportedAsync(database.Set("Holder"), ValidRecord(database.Schema));
        await ExpectNotSupportedAsync(database.Set<Holder>(), new Holder { Id = 1 });

        // Adding to a write batch validates and copies; it writes nothing.
        var batch = database.CreateWriteBatch();
        batch.Insert(ValidRecord(database.Schema));
        Assert.Equal(1, batch.Count);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // A field, an owned value and a field inside it, without a value.
    [InlineData("Tags is missing")]
    [InlineData("Work is missing")]
    [InlineData("City of Work is missing")]
    [InlineData("City of a Place is missing")]
    // Made invalid through what the record returned.
    [InlineData("a null is added to the returned Tags")]
    [InlineData("a null value is added to the returned Options")]
    [InlineData("City of the returned Work is set to null")]
    public async Task Record_with_an_invalid_value_at_any_depth_is_ValidationFailed_before_the_write_is_refused(string fault)
    {
        await using var database = await TestData.OpenMasterAsync(schema: NestedSchema());
        var record = ValidRecord(database.Schema);
        Assert.True(database.Schema.TryGetOwnedType("Address", out var address));

        switch (fault)
        {
            case "Tags is missing":
                record["Tags"] = null;
                break;
            case "Work is missing":
                record["Work"] = null;
                break;
            case "City of Work is missing":
                record["Work"] = new LatticeOwnedValue(address) { ["Lines"] = Array.Empty<string>() };
                break;
            case "City of a Place is missing":
                record["Places"] = new[] { new LatticeOwnedValue(address) { ["Lines"] = Array.Empty<string>() } };
                break;
            case "a null is added to the returned Tags":
                record.Get<List<string>>("Tags")!.Add(null!);
                break;
            case "a null value is added to the returned Options":
                record.Get<Dictionary<string, string>>("Options")!["Theme"] = null!;
                break;
            case "City of the returned Work is set to null":
                record.Get<LatticeOwnedValue>("Work")!["City"] = null;
                break;
        }

        await ExpectValidationFailedAsync(database, database.Set("Holder"), record, (batch, value) => batch.Insert(value));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Record_made_invalid_through_a_collection_of_an_owned_value_it_returned_is_ValidationFailed_when_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: NestedSchema());
        var record = ValidRecord(database.Schema);

        // The owned value Work returns its own Lines; a null added there changes the record
        // without a check.
        record.Get<LatticeOwnedValue>("Work")!.Get<List<string>>("Lines")!.Add(null!);

        await ExpectValidationFailedAsync(database, database.Set("Holder"), record, (batch, value) => batch.Insert(value));
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    [InlineData("a null in Tags")]
    [InlineData("Work is null")]
    [InlineData("City of Work is null")]
    [InlineData("a null in Lines of Work")]
    [InlineData("a null value in Options")]
    [InlineData("a null in Rights")]
    [InlineData("a null in Places")]
    [InlineData("City of a Place is null")]
    [InlineData("a key twice in Counts")]
    public async Task Object_with_an_invalid_value_at_any_depth_is_ValidationFailed_before_the_write_is_refused(string fault)
    {
        await using var database = await TestData.OpenMasterAsync(schema: NestedSchema());
        var holder = new Holder { Id = 1 };

        switch (fault)
        {
            case "a null in Tags":
                holder.Tags = ["a", null!];
                break;
            case "Work is null":
                holder.Work = null!;
                break;
            case "City of Work is null":
                holder.Work.City = null!;
                break;
            case "a null in Lines of Work":
                holder.Work.Lines = [null!];
                break;
            case "a null value in Options":
                holder.Options = new() { ["Theme"] = null! };
                break;
            case "a null in Rights":
                holder.Rights = ["read", null!];
                break;
            case "a null in Places":
                holder.Places = [new Address(), null!];
                break;
            case "City of a Place is null":
                holder.Places = [new Address { City = null! }];
                break;
            case "a key twice in Counts":
                // Tag compares its key without case.
                holder.Counts = new(StringComparer.Ordinal) { ["Main"] = 1, ["main"] = 2 };
                break;
        }

        await ExpectValidationFailedAsync(database, database.Set<Holder>(), holder, (batch, value) => batch.Insert(value));
    }

    [Theory]
    [InlineData("Unique")]
    [InlineData("Folded")]
    [InlineData("Pooled")]
    [InlineData("Graded")]
    public async Task Record_of_an_entity_type_that_the_placeholder_refuses_is_validated_first(string entityTypeName)
    {
        await using var database = await TestData.OpenMasterAsync(schema: RefusedSchema());

        await ExpectValidatedFirstAsync(database, entityTypeName);
    }

    [Fact]
    public async Task Record_in_a_schema_of_the_UTF16_encoding_is_validated_first()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Utf16Schema());

        await ExpectValidatedFirstAsync(database, "Note");
    }

    [Fact]
    public async Task Object_of_an_entity_type_that_the_placeholder_refuses_is_validated_first()
    {
        await using var database = await TestData.OpenMasterAsync(schema: RefusedSchema());
        var unique = database.Set<UniqueItem>();
        var graded = database.Set<GradedItem>();

        // Without a required value.
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => unique.InsertAsync(new UniqueItem { Id = 1, Text = null! }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => graded.UpsertAsync(new GradedItem { Id = 1, Text = null! }));

        // A key of another type.
        await Assert.ThrowsAsync<ArgumentException>(async () => await unique.DeleteAsync("one"));

        // Complete.
        await Assert.ThrowsAsync<NotSupportedException>(async () => await unique.InsertAsync(new UniqueItem { Id = 1, Text = "a" }));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await graded.InsertAsync(new GradedItem { Id = 1, Text = "a", Level = 1 }));
    }

    [Fact]
    public async Task Integer_that_its_enum_type_does_not_declare_is_not_refused_yet_and_the_write_ends_as_not_implemented()
    {
        await using var database = await TestData.OpenMasterAsync(schema: RefusedSchema());
        var graded = database.Set("Graded");

        // Level declares 0 and 1; whether a value is declared is checked when enum fields are
        // stored.
        var record = new LatticeRecord(graded.EntityType) { ["Id"] = 1L, ["Text"] = "a", ["Level"] = (byte)7 };

        await Assert.ThrowsAsync<NotSupportedException>(async () => await graded.InsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await database.Set<GradedItem>().InsertAsync(new GradedItem { Id = 1, Text = "a", Level = 7 }));
    }

    // A record without the required Text, a key of another type, and a complete record, through
    // a set and through a write batch.
    private static async Task ExpectValidatedFirstAsync(ILatticeDatabase database, string entityTypeName)
    {
        var set = database.Set(entityTypeName);
        var incomplete = new LatticeRecord(set.EntityType) { ["Id"] = 1L };
        if (set.EntityType.TryGetField("Level", out _))
        {
            incomplete["Level"] = (byte)1;
        }

        await ExpectValidationFailedAsync(database, set, incomplete, (batch, record) => batch.Upsert(record));

        await Assert.ThrowsAsync<ArgumentException>(async () => await set.DeleteAsync("one"));
        Assert.Throws<ArgumentException>(() => database.CreateWriteBatch().Delete(entityTypeName, "one"));

        incomplete["Text"] = "complete";
        await ExpectNotSupportedAsync(set, incomplete);
    }

    private static async Task ExpectValidationFailedAsync(ILatticeDatabase database, ILatticeSet set, LatticeRecord record, Action<ILatticeWriteBatch, LatticeRecord> add)
    {
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => set.InsertAsync(record));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => set.UpsertAsync(record));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => set.ReplaceAsync(record));

        var batch = database.CreateWriteBatch();
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => add(batch, record));
        Assert.Equal(0, batch.Count);
    }

    private static async Task ExpectValidationFailedAsync<TEntity>(ILatticeDatabase database, ILatticeSet<TEntity> set, TEntity entity, Action<ILatticeWriteBatch, TEntity> add)
        where TEntity : class
    {
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => set.InsertAsync(entity));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => set.UpsertAsync(entity));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => set.ReplaceAsync(entity));

        var batch = database.CreateWriteBatch();
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => add(batch, entity));
        Assert.Equal(0, batch.Count);
    }

    private static async Task ExpectNotSupportedAsync(ILatticeSet set, LatticeRecord record)
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.InsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.UpsertAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.ReplaceAsync(record));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.DeleteAsync(1L));
    }

    private static async Task ExpectNotSupportedAsync<TEntity>(ILatticeSet<TEntity> set, TEntity entity)
        where TEntity : class
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.InsertAsync(entity));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.UpsertAsync(entity));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.ReplaceAsync(entity));
        await Assert.ThrowsAsync<NotSupportedException>(async () => await set.DeleteAsync(1L));
    }

    // A record of Holder with every field that is not nullable set to a valid value.
    private static LatticeRecord ValidRecord(LatticeSchema schema)
    {
        Assert.True(schema.TryGetEntityType("Holder", out var holder));
        Assert.True(schema.TryGetOwnedType("Address", out var address));

        return new LatticeRecord(holder)
        {
            ["Id"] = 1L,
            ["Work"] = new LatticeOwnedValue(address) { ["City"] = "Oslo", ["Lines"] = new[] { "Karl Johans gate 1" } },
            ["Tags"] = new[] { "a" },
            ["Scores"] = new int?[] { 1, null },
            ["Options"] = new Dictionary<string, string> { ["Mode"] = "dark" },
            ["Rights"] = new[] { "read" },
            ["Places"] = Array.Empty<LatticeOwnedValue>(),
            ["Counts"] = new Dictionary<string, int> { ["Main"] = 1 },
        };
    }

    // Holder has owned values, collections and a Map of references to Tag, whose key is
    // compared without case. Holder itself declares nothing that Epic 1, sprint 2 refuses.
    private static LatticeSchema NestedSchema() => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "{{typeof(Address).FullName}}",
              "Fields": [ { "Id": 1, "Name": "City", "Type": "String" }, { "Id": 2, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } } ]
            }
          ],
          "EntityTypes": [
            { "Id": 1, "Name": "Tag", "Fields": [ { "Id": 1, "Name": "Name", "Type": "String", "Comparison": "OrdinalIgnoreCase" } ], "PrimaryKey": [1] },
            {
              "Id": 2, "Name": "Holder", "ClrType": "{{typeof(Holder).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Work", "Type": "Owned", "OwnedType": "Address" },
                { "Id": 3, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 4, "Name": "Tags", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 5, "Name": "Scores", "Type": "List", "Element": { "Type": "Int32", "Nullable": true } },
                { "Id": 6, "Name": "Options", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "String" } },
                { "Id": 7, "Name": "Rights", "Type": "Set", "Element": { "Type": "String" } },
                { "Id": 8, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 9, "Name": "Counts", "Type": "Map", "Key": { "Type": "Reference", "EntityType": "Tag" }, "Value": { "Type": "Int32" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // One entity type for each declaration of Epic 1, sprint 2, that the placeholder refuses to
    // write; each has a required Text.
    private static LatticeSchema RefusedSchema() => LatticeSchema.Compile($$"""
        {
          "Enums": [
            { "Id": 1, "Name": "Level", "UnderlyingType": "UInt8", "Members": [ { "Name": "Low", "Value": 0 }, { "Name": "High", "Value": 1 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Unique", "ClrType": "{{typeof(UniqueItem).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByText", "Fields": [2], "Unique": true } ]
            },
            {
              "Id": 2, "Name": "Folded",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String", "Comparison": "OrdinalIgnoreCase" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 3, "Name": "Pooled",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String", "StringStorage": "Dictionary" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 4, "Name": "Graded", "ClrType": "{{typeof(GradedItem).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" }, { "Id": 3, "Name": "Level", "Type": "Enum", "Enum": "Level" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private static LatticeSchema Utf16Schema() => LatticeSchema.Compile("""
        {
          "StringEncoding": "Utf16",
          "EntityTypes": [
            { "Id": 1, "Name": "Note", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ], "PrimaryKey": [1] }
          ]
        }
        """);

    private sealed class Holder
    {
        public long Id { get; set; }

        public Address Work { get; set; } = new();

        public Address? Home { get; set; }

        public List<string> Tags { get; set; } = [];

        public List<int?> Scores { get; set; } = [];

        public Dictionary<string, string> Options { get; set; } = [];

        public HashSet<string> Rights { get; set; } = [];

        public List<Address> Places { get; set; } = [];

        public Dictionary<string, int> Counts { get; set; } = [];
    }

    private sealed class Address
    {
        public string City { get; set; } = "Oslo";

        public List<string> Lines { get; set; } = [];
    }

    private sealed class UniqueItem
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }

    private sealed class GradedItem
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";

        public byte Level { get; set; }
    }
}
