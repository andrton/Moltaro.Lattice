namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// The ordinary set operations of an entity type with a Map or Set, through both API forms.
// Each entity type has an Int64 key, a required Text with the index ByText and a collection Value.
public sealed class CollectionSetOperationsTests
{
    private static readonly Dictionary<string, Case> Cases = new Case[]
    {
        Case.WithValue<Dictionary<string, int>>(
            "Map field",
            value: "'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Int32' }",
            recordValue: _ => new Dictionary<string, int> { ["a"] = 1 },
            classValue: () => new() { ["a"] = 1 }),
        Case.WithValue<HashSet<int>>("Set field", value: "'Type': 'Set', 'Element': { 'Type': 'Int32' }", recordValue: _ => new[] { 1 }, classValue: () => [1]),
    }.ToDictionary(@case => @case.Name);

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task A_write_checks_its_arguments_and_validates_an_entity_with_a_collection(string caseName)
    {
        var @case = Cases[caseName];
        await using var database = await TestDatabases.OpenMasterAsync(@case.Schema);
        var items = database.Set("Item");

        // Text is required.
        var incomplete = @case.CompleteRecord(items.EntityType, database.Schema);
        incomplete["Text"] = null;

        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => items.InsertAsync(incomplete));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => items.ReplaceAsync(incomplete));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => items.UpsertAsync(incomplete));

        // No key type of the catalog is a floating-point number.
        await Assert.ThrowsAsync<ArgumentException>(async () => await items.DeleteAsync(1.5));
        await Assert.ThrowsAsync<ArgumentException>(async () => await items.DeleteAsync(1.5, default(LatticeRevision)));

        await @case.Class.ExpectValidatedFirstAsync(database);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task An_empty_entity_type_with_a_collection_has_no_entities_to_read(string caseName)
    {
        var @case = Cases[caseName];
        await using var database = await TestDatabases.OpenMasterAsync(@case.Schema);
        var items = database.Set("Item");

        Assert.Null(items.Get(@case.Key));
        Assert.False(items.TryGet(@case.Key, out _));
        Assert.False(items.TryGet(@case.Key, out _, out _));
        Assert.False((await items.GetAsync(@case.Key, new LatticeSession())).Found);
        Assert.Empty(items.Enumerate());
        Assert.Empty(await items.EnumerateAsync(new LatticeSession()).ToListAsync());

        await @case.Class.ExpectNothingReadAsync(database);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task An_entity_type_with_a_collection_binds_and_its_index_is_selected(string caseName)
    {
        var @case = Cases[caseName];
        await using var database = await TestDatabases.OpenMasterAsync(@case.Schema);

        Assert.NotNull(database.Set("Item").Index("ByText"));

        @case.Class.ExpectBound(database);
    }

    // One case: its schema, a key of its key type, and what makes a complete record and a
    // complete object of Item.
    private sealed record Case(string Name, LatticeSchema Schema, object Key, Func<LatticeSchema, object> RecordValue, ItemClass Class)
    {
        public LatticeRecord CompleteRecord(LatticeEntityType entityType, LatticeSchema schema) =>
            new(entityType) { ["Id"] = Key, ["Text"] = "a", ["Value"] = RecordValue(schema) };

        // A case whose Value is of another type.
        public static Case WithValue<TValue>(string name, string value, Func<LatticeSchema, object> recordValue, Func<TValue> classValue) =>
            Of(name, "'Type': 'Int64'", 1L, "", value, recordValue, classValue);

        private static Case Of<TKey, TValue>(
            string name,
            string key,
            TKey keyValue,
            string text,
            string value,
            Func<LatticeSchema, object> recordValue,
            Func<TValue> classValue)
            where TKey : notnull
        {
            var json = $$"""
                {
                  "EntityTypes": [
                    {
                      "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item<TKey, TValue>).FullName}}",
                      "Fields": [
                        { "Id": 1, "Name": "Id", {{key}} },
                        { "Id": 2, "Name": "Text", "Type": "String"{{text}} },
                        { "Id": 3, "Name": "Value", {{value}} }
                      ],
                      "PrimaryKey": [1],
                      "Indexes": [ { "Id": 1, "Name": "ByText", "Fields": [2] } ]
                    }
                  ]
                }
                """;

            return new Case(name, LatticeSchema.Compile(json.Replace('\'', '"')), keyValue, recordValue, new ItemClass<TKey, TValue>(keyValue, classValue));
        }
    }

    // The class Item<TKey, TValue> of a case, so that a test reaches it without naming the type
    // arguments of the case.
    private abstract class ItemClass
    {
        public abstract Task ExpectValidatedFirstAsync(ILatticeDatabase database);

        public abstract Task ExpectNothingReadAsync(ILatticeDatabase database);

        public abstract void ExpectBound(ILatticeDatabase database);
    }

    private sealed class ItemClass<TKey, TValue>(TKey key, Func<TValue> value) : ItemClass
        where TKey : notnull
    {
        public override async Task ExpectValidatedFirstAsync(ILatticeDatabase database)
        {
            var items = database.Set<Item<TKey, TValue>>();
            var incomplete = new Item<TKey, TValue> { Id = key, Text = null!, Value = value() };

            await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => items.InsertAsync(incomplete));
            await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => items.ReplaceAsync(incomplete));
            await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => items.UpsertAsync(incomplete));
            await Assert.ThrowsAsync<ArgumentException>(async () => await items.DeleteAsync(1.5));
            await Assert.ThrowsAsync<ArgumentException>(async () => await items.DeleteAsync(1.5, default(LatticeRevision)));
        }

        public override async Task ExpectNothingReadAsync(ILatticeDatabase database)
        {
            var items = database.Set<Item<TKey, TValue>>();

            Assert.Null(items.Get(key));
            Assert.False(items.TryGet(key, out _));
            Assert.False(items.TryGet(key, out _, out _));
            Assert.False((await items.GetAsync(key, new LatticeSession())).Found);
            Assert.Empty(items.Enumerate());
            Assert.Empty(await items.EnumerateAsync(new LatticeSession()).ToListAsync());
        }

        public override void ExpectBound(ILatticeDatabase database)
        {
            var items = database.Set<Item<TKey, TValue>>();

            Assert.Equal("Item", items.EntityType.Name);
            Assert.NotNull(items.Index("ByText"));
        }
    }

    // The class of Item in every case: the key and Value of the case, and Text.
    private sealed class Item<TKey, TValue>
    {
        public TKey Id { get; set; } = default!;

        public string Text { get; set; } = "";

        public TValue Value { get; set; } = default!;
    }
}
