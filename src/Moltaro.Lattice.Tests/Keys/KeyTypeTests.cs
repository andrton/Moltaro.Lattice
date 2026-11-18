namespace Moltaro.Lattice.Tests.Keys;

// Every key type of the catalog is a primary key (Q03.4.1; brief of Epic 1, sprint S07): the eight
// integers, String and Guid. Under each an entity is inserted, read, replaced, upserted, deleted and
// enumerated, through its class and by name. The keys stored
// are the smallest and the largest value of an integer type, so that one of the UInt64 keys is above
// the largest Int64; for String the empty key and another; for Guid two.
//
// A key argument is the type its field declares, or an integer of another width when it fits, which
// finds its entity. A key that does not fit, a negative one for an unsigned key included, a text for
// a Guid key and a key of another type are ArgumentException, for a read and a delete, through the
// class and by name, and for a delete added to a batch; no text is parsed. The keys that do not fit
// are the largest value of the key type plus one and the smallest minus one, each in the narrowest
// integer type that holds it: an unchecked conversion turns each of them into the other stored key,
// so a conversion that cuts a key to the width of its field, or reads its bits as another type,
// instead of refusing it finds an entity and fails the test.
//
// Each key type has an entity type Item of its own, in a schema of its own: the key Id and the
// String Text, bound to the class Item<TKey>.
public sealed class KeyTypeTests
{
    // The eight integer types, from the narrowest, with their ranges and a conversion from Int128.
    private static readonly IntegerType[] IntegerTypes =
    [
        new(typeof(sbyte), sbyte.MinValue, sbyte.MaxValue, value => (sbyte)value),
        new(typeof(byte), byte.MinValue, byte.MaxValue, value => (byte)value),
        new(typeof(short), short.MinValue, short.MaxValue, value => (short)value),
        new(typeof(ushort), ushort.MinValue, ushort.MaxValue, value => (ushort)value),
        new(typeof(int), int.MinValue, int.MaxValue, value => (int)value),
        new(typeof(uint), uint.MinValue, uint.MaxValue, value => (uint)value),
        new(typeof(long), long.MinValue, long.MaxValue, value => (long)value),
        new(typeof(ulong), ulong.MinValue, ulong.MaxValue, value => (ulong)value),
    ];

    private static readonly Dictionary<string, KeyCase> Cases = new KeyCase[]
    {
        KeyCase.Integer<sbyte>("Int8"),
        KeyCase.Integer<byte>("UInt8"),
        KeyCase.Integer<short>("Int16"),
        KeyCase.Integer<ushort>("UInt16"),
        KeyCase.Integer<int>("Int32"),
        KeyCase.Integer<uint>("UInt32"),
        KeyCase.Integer<long>("Int64"),
        KeyCase.Integer<ulong>("UInt64"),
        KeyCase.Of("String", first: "", second: "key", absent: "Key", otherTypes: [1, Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")]),
        KeyCase.Of(
            "Guid",
            first: Guid.Parse("5f0c1b8e-58a4-4c4e-9a43-2f6f1c0d7e11"),
            second: Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7"),
            absent: Guid.Parse("e5b1c2d3-1111-4a2b-8c3d-0123456789ab"),
            otherTypes: ["5f0c1b8e-58a4-4c4e-9a43-2f6f1c0d7e11", 1]),
    }.ToDictionary(@case => @case.Name);

    public static IEnumerable<TheoryDataRow<string>> KeyTypes =>
        Cases.Keys.Select(keyType => new TheoryDataRow<string>(keyType));

    [Theory]
    [MemberData(nameof(KeyTypes))]
    public async Task Entity_is_inserted_read_replaced_upserted_deleted_and_enumerated_under_every_key_type(string keyType)
    {
        var @case = Cases[keyType];
        await using var database = await TestDatabases.OpenMasterAsync(@case.Schema);

        await @case.ExpectEveryOperationAsync(database);
    }

    [Theory]
    [MemberData(nameof(KeyTypes))]
    public async Task Key_of_another_width_that_fits_finds_its_entity_and_one_that_does_not_fit_or_is_of_another_type_is_ArgumentException(string keyType)
    {
        var @case = Cases[keyType];
        await using var database = await TestDatabases.OpenMasterAsync(@case.Schema);

        await @case.ExpectKeyArgumentsAsync(database);
    }

    // An integer type of .NET: its range, and the value of the type for an Int128 inside it.
    private sealed record IntegerType(Type Type, Int128 Min, Int128 Max, Func<Int128, object> Of)
    {
        public bool Holds(Int128 value) => Min <= value && value <= Max;
    }

    // One key type: the schema of its Item, two keys that are stored and one that is not, and the key
    // arguments that find each stored key and those that are refused.
    private abstract class KeyCase(string name, LatticeSchema schema)
    {
        public string Name { get; } = name;

        public LatticeSchema Schema { get; } = schema;

        // The smallest and the largest value of the integer type. The keys that fit are each of the two
        // in every integer type that holds it; the keys that do not fit are the largest value plus one
        // and the smallest minus one in the narrowest type that holds them, where one does; the keys of
        // another type are a text, a floating-point number and a Guid.
        public static KeyCase Integer<TKey>(string name)
            where TKey : notnull
        {
            var type = Array.Find(IntegerTypes, integerType => integerType.Type == typeof(TKey))!;
            var fitting = new List<(object Key, object Stored)>();

            foreach (var other in IntegerTypes)
            {
                if (other.Holds(type.Min))
                {
                    fitting.Add((other.Of(type.Min), type.Of(type.Min)));
                }

                if (other.Holds(type.Max))
                {
                    fitting.Add((other.Of(type.Max), type.Of(type.Max)));
                }
            }

            var refused = new List<object>();

            foreach (var outside in new[] { type.Max + 1, type.Min - 1 })
            {
                if (Array.Find(IntegerTypes, integerType => integerType.Holds(outside)) is { } narrowest)
                {
                    refused.Add(narrowest.Of(outside));
                }
            }

            refused.AddRange(["1", 1.0, Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e")]);

            return new KeyCase<TKey>(name, (TKey)type.Of(type.Min), (TKey)type.Of(type.Max), (TKey)type.Of(1), fitting, refused);
        }

        // A key type whose keys are given only as their own type.
        public static KeyCase Of<TKey>(string name, TKey first, TKey second, TKey absent, object[] otherTypes)
            where TKey : notnull =>
            new KeyCase<TKey>(name, first, second, absent, [(first, first), (second, second)], [.. otherTypes]);

        public abstract Task ExpectEveryOperationAsync(ILatticeDatabase database);

        public abstract Task ExpectKeyArgumentsAsync(ILatticeDatabase database);

        protected static LatticeSchema SchemaOf<TKey>(string keyType) => LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item<TKey>).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "{{keyType}}" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
    }

    private sealed class KeyCase<TKey>(string name, TKey first, TKey second, TKey absent, List<(object Key, object Stored)> fitting, List<object> refused)
        : KeyCase(name, SchemaOf<TKey>(name))
        where TKey : notnull
    {
        public override async Task ExpectEveryOperationAsync(ILatticeDatabase database)
        {
            var items = database.Set<Item<TKey>>();
            var records = database.Set("Item");

            // Insert: the first key through the class, the second by name.
            await items.InsertAsync(new Item<TKey> { Id = first, Text = "first" });
            await records.InsertAsync(RecordOf(records, second, "second"));

            // Read, through the class and by name, with and without a session; the absent key finds
            // nothing.
            Assert.Equal(first, items.Get(first)!.Id);
            Assert.Equal("first", items.Get(first)!.Text);
            Assert.Equal((object)second, records.Get(second)!["Id"]);
            Assert.Equal("second", records.Get(second)!["Text"]);
            Assert.True(items.TryGet(second, out var stored, out var revision));
            Assert.Equal("second", stored.Text);
            Assert.Equal("first", (await records.GetAsync(first, new LatticeSession())).Entity!["Text"]);
            Assert.Equal("second", (await items.GetAsync(second, new LatticeSession())).Entity!.Text);
            Assert.Null(items.Get(absent));
            Assert.Null(records.Get(absent));

            // Replace, through the class with the revision read, and by name.
            await items.ReplaceAsync(new Item<TKey> { Id = second, Text = "second, replaced" }, revision);
            await records.ReplaceAsync(RecordOf(records, first, "first, replaced"));

            Assert.Equal("second, replaced", items.Get(second)!.Text);
            Assert.Equal("first, replaced", records.Get(first)!["Text"]);

            // Upsert of a stored key by name, and of the absent one through the class.
            await records.UpsertAsync(RecordOf(records, second, "second, upserted"));
            await items.UpsertAsync(new Item<TKey> { Id = absent, Text = "third" });

            Assert.Equal("second, upserted", items.Get(second)!.Text);
            Assert.Equal("third", records.Get(absent)!["Text"]);

            // Delete through the class and by name; a key deleted already deletes nothing.
            Assert.True((await items.DeleteAsync(second)).Changed);
            Assert.False((await records.DeleteAsync(second)).Changed);
            Assert.True((await records.DeleteAsync(absent)).Changed);

            // Enumerate: the first entity is the one left, through the class and by name.
            var left = Assert.Single(items.Enumerate());
            Assert.Equal(first, left.Id);
            Assert.Equal("first, replaced", left.Text);
            var leftRecord = Assert.Single(records.Enumerate());
            Assert.Equal((object)first, leftRecord["Id"]);
        }

        public override async Task ExpectKeyArgumentsAsync(ILatticeDatabase database)
        {
            var items = database.Set<Item<TKey>>();
            var records = database.Set("Item");
            await items.InsertAsync(new Item<TKey> { Id = first, Text = "first" });
            await items.InsertAsync(new Item<TKey> { Id = second, Text = "second" });

            // Each key that fits finds the stored key it is equal to, through the class, given as its
            // own type, and by name.
            foreach (var (key, storedKey) in fitting)
            {
                var found = GetAs(items, key);
                var foundRecord = records.Get(key);

                Assert.NotNull(found);
                Assert.Equal(storedKey, (object)found.Id);
                Assert.NotNull(foundRecord);
                Assert.Equal(storedKey, foundRecord["Id"]);
            }

            // Each refused key is an ArgumentException for a read and a delete, through the class and by
            // name, and for a delete added to a batch.
            var batch = database.CreateWriteBatch();

            foreach (var key in refused)
            {
                Assert.Throws<ArgumentException>(() => GetAs(items, key));
                Assert.Throws<ArgumentException>(() => records.Get(key));
                await Assert.ThrowsAsync<ArgumentException>(async () => await DeleteAs(items, key));
                await Assert.ThrowsAsync<ArgumentException>(async () => await records.DeleteAsync(key));
                Assert.Throws<ArgumentException>(() => batch.Delete("Item", key));
            }

            // Nothing was deleted.
            Assert.Equal(0, batch.Count);
            Assert.Equal("first", items.Get(first)!.Text);
            Assert.Equal("second", items.Get(second)!.Text);
        }

        private static LatticeRecord RecordOf(ILatticeSet records, TKey key, string text) =>
            new(records.EntityType) { ["Id"] = key, ["Text"] = text };

        // A read through the class with the key given as its own type.
        private static Item<TKey>? GetAs(ILatticeSet<Item<TKey>> items, object key) => key switch
        {
            sbyte value => items.Get(value),
            byte value => items.Get(value),
            short value => items.Get(value),
            ushort value => items.Get(value),
            int value => items.Get(value),
            uint value => items.Get(value),
            long value => items.Get(value),
            ulong value => items.Get(value),
            double value => items.Get(value),
            string value => items.Get(value),
            Guid value => items.Get(value),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No read for a key of this type."),
        };

        // A delete through the class with the key given as its own type.
        private static ValueTask<LatticeWriteResult> DeleteAs(ILatticeSet<Item<TKey>> items, object key) => key switch
        {
            sbyte value => items.DeleteAsync(value),
            byte value => items.DeleteAsync(value),
            short value => items.DeleteAsync(value),
            ushort value => items.DeleteAsync(value),
            int value => items.DeleteAsync(value),
            uint value => items.DeleteAsync(value),
            long value => items.DeleteAsync(value),
            ulong value => items.DeleteAsync(value),
            double value => items.DeleteAsync(value),
            string value => items.DeleteAsync(value),
            Guid value => items.DeleteAsync(value),
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No delete for a key of this type."),
        };
    }

    // The class of Item for each key type.
    private sealed class Item<TKey>
    {
        public TKey Id { get; set; } = default!;

        public string Text { get; set; } = "";
    }
}
