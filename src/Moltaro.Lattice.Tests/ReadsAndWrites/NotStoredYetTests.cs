namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// What the engine cannot store until a later sprint, and what it does with such an entity type
// (brief of Epic 1, sprint S06, "What the engine does where the block above does not say"). From S07
// it stores every key type of the catalog, a String compared with either comparison, an Enum field
// and both encodings (brief of S07), and it still refuses, as S06 says: Dictionary storage until S13,
// an entity type with a unique index until S16, and the field types of S03 until S18 and S19. Every write of such an entity type, an insert, a replace, an
// upsert and a delete, with or without a revision, is refused with NotSupportedException at the
// write, after the checks of its arguments and the validation of the entity, in the order of Q25. A
// read of it by key finds nothing and an enumeration of it is empty. Binding a class to it and
// selecting one of its indexes work.
//
// Each case is one entity type, Item, in a schema of its own: the key Id, an Int64, the String field
// Text with the index ByText, and the field Value. Where the case is not Value, Value is an Int32.
public sealed class NotStoredYetTests
{
    private static readonly Dictionary<string, Case> Cases = new Case[]
    {
        Case.Of("Text in Dictionary storage", text: ", 'StringStorage': 'Dictionary'"),
        Case.Of("Unique index", unique: true),
        Case.WithValue<Stamp>(
            "Owned field",
            value: "'Type': 'Owned', 'OwnedType': 'Stamp'",
            recordValue: schema => new LatticeOwnedValue(OwnedStamp(schema)) { ["Text"] = "a" },
            classValue: () => new Stamp { Text = "a" }),
        Case.WithValue<List<string>>("List field", value: "'Type': 'List', 'Element': { 'Type': 'String' }", recordValue: _ => new[] { "a" }, classValue: () => ["a"]),
        Case.WithValue<Dictionary<string, int>>(
            "Map field",
            value: "'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Int32' }",
            recordValue: _ => new Dictionary<string, int> { ["a"] = 1 },
            classValue: () => new() { ["a"] = 1 }),
        Case.WithValue<HashSet<int>>("Set field", value: "'Type': 'Set', 'Element': { 'Type': 'Int32' }", recordValue: _ => new[] { 1 }, classValue: () => [1]),
        Case.WithValue<long>("Reference field", value: "'Type': 'Reference', 'EntityType': 'Item'", recordValue: _ => 2L, classValue: () => 2L),
    }.ToDictionary(@case => @case.Name);

    public static TheoryData<string> CaseNames => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Every_write_of_an_entity_type_the_engine_cannot_store_yet_is_not_supported(string caseName)
    {
        var @case = Cases[caseName];
        await using var database = await TestDatabases.OpenMasterAsync(@case.Schema);
        var items = database.Set("Item");
        var record = @case.CompleteRecord(items.EntityType, database.Schema);

        foreach (var write in EveryWrite(items, record, @case.Key))
        {
            await Assert.ThrowsAsync<NotSupportedException>(async () => await write());
        }

        await @case.Class.ExpectEveryWriteNotSupportedAsync(database);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public async Task Write_of_such_an_entity_type_checks_its_arguments_and_validates_the_entity_before_it_is_refused(string caseName)
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
    public async Task Read_of_such_an_entity_type_by_key_finds_nothing_and_an_enumeration_of_it_is_empty(string caseName)
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
    public async Task Class_of_such_an_entity_type_binds_and_its_index_is_selected(string caseName)
    {
        var @case = Cases[caseName];
        await using var database = await TestDatabases.OpenMasterAsync(@case.Schema);

        Assert.NotNull(database.Set("Item").Index("ByText"));

        @case.Class.ExpectBound(database);
    }

    // Every write of the set, without and with a revision and a session.
    private static Func<ValueTask<LatticeWriteResult>>[] EveryWrite(ILatticeSet items, LatticeRecord record, object key) =>
    [
        () => items.InsertAsync(record),
        () => items.InsertAsync(record, new LatticeSession()),
        () => items.ReplaceAsync(record),
        () => items.ReplaceAsync(record, new LatticeSession()),
        () => items.ReplaceAsync(record, default(LatticeRevision)),
        () => items.ReplaceAsync(record, default(LatticeRevision), new LatticeSession()),
        () => items.UpsertAsync(record),
        () => items.UpsertAsync(record, new LatticeSession()),
        () => items.DeleteAsync(key),
        () => items.DeleteAsync(key, new LatticeSession()),
        () => items.DeleteAsync(key, default(LatticeRevision)),
        () => items.DeleteAsync(key, default(LatticeRevision), new LatticeSession()),
    ];

    private static LatticeOwnedType OwnedStamp(LatticeSchema schema)
    {
        Assert.True(schema.TryGetOwnedType("Stamp", out var stamp));
        return stamp;
    }

    // One case: its schema, a key of its key type, and what makes a complete record and a
    // complete object of Item.
    private sealed record Case(string Name, LatticeSchema Schema, object Key, Func<LatticeSchema, object> RecordValue, ItemClass Class)
    {
        public LatticeRecord CompleteRecord(LatticeEntityType entityType, LatticeSchema schema) =>
            new(entityType) { ["Id"] = Key, ["Text"] = "a", ["Value"] = RecordValue(schema) };

        // A case with an Int32 Value, which differs from what the engine stores in its Text field or
        // its index.
        public static Case Of(string name, string text = "", bool unique = false) =>
            Of<long, int>(name, "'Type': 'Int64'", 1L, text, "'Type': 'Int32'", _ => 7, () => 7, unique);

        // A case whose Value is of another type.
        public static Case WithValue<TValue>(string name, string value, Func<LatticeSchema, object> recordValue, Func<TValue> classValue) =>
            Of(name, "'Type': 'Int64'", 1L, "", value, recordValue, classValue, false);

        private static Case Of<TKey, TValue>(
            string name,
            string key,
            TKey keyValue,
            string text,
            string value,
            Func<LatticeSchema, object> recordValue,
            Func<TValue> classValue,
            bool unique)
            where TKey : notnull
        {
            var json = $$"""
                {
                  "OwnedTypes": [
                    { "Id": 1, "Name": "Stamp", "ClrType": "{{typeof(Stamp).FullName}}", "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" } ] }
                  ],
                  "EntityTypes": [
                    {
                      "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item<TKey, TValue>).FullName}}",
                      "Fields": [
                        { "Id": 1, "Name": "Id", {{key}} },
                        { "Id": 2, "Name": "Text", "Type": "String"{{text}} },
                        { "Id": 3, "Name": "Value", {{value}} }
                      ],
                      "PrimaryKey": [1],
                      "Indexes": [ { "Id": 1, "Name": "ByText", "Fields": [2]{{(unique ? ", 'Unique': true" : "")}} } ]
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
        public abstract Task ExpectEveryWriteNotSupportedAsync(ILatticeDatabase database);

        public abstract Task ExpectValidatedFirstAsync(ILatticeDatabase database);

        public abstract Task ExpectNothingReadAsync(ILatticeDatabase database);

        public abstract void ExpectBound(ILatticeDatabase database);
    }

    private sealed class ItemClass<TKey, TValue>(TKey key, Func<TValue> value) : ItemClass
        where TKey : notnull
    {
        public override async Task ExpectEveryWriteNotSupportedAsync(ILatticeDatabase database)
        {
            var items = database.Set<Item<TKey, TValue>>();
            var item = new Item<TKey, TValue> { Id = key, Text = "a", Value = value() };

            Func<ValueTask<LatticeWriteResult>>[] writes =
            [
                () => items.InsertAsync(item),
                () => items.InsertAsync(item, new LatticeSession()),
                () => items.ReplaceAsync(item),
                () => items.ReplaceAsync(item, new LatticeSession()),
                () => items.ReplaceAsync(item, default(LatticeRevision)),
                () => items.ReplaceAsync(item, default(LatticeRevision), new LatticeSession()),
                () => items.UpsertAsync(item),
                () => items.UpsertAsync(item, new LatticeSession()),
                () => items.DeleteAsync(key),
                () => items.DeleteAsync(key, new LatticeSession()),
                () => items.DeleteAsync(key, default(LatticeRevision)),
                () => items.DeleteAsync(key, default(LatticeRevision), new LatticeSession()),
            ];

            foreach (var write in writes)
            {
                await Assert.ThrowsAsync<NotSupportedException>(async () => await write());
            }
        }

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

    // The class of the owned type Stamp.
    private sealed class Stamp
    {
        public string Text { get; set; } = "";
    }
}
