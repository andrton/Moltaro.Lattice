namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A value is found the same way however it is looked for: for every scalar and key family the same value
// is found by key, by index, through a class and by name (IndexKey), at the smallest and the largest value
// of its type, where a value cut to another width or read with the sign of another type is another value:
// - for each key family, the eight integers, String and Guid, an entity of Keyed is read by its key and
//   found by an index on its Value, a field of the same type that holds the same value: the read and the
//   search, through the class and by name, by Find and by FindAsync, find the same entity. The smallest
//   String is the empty text, the largest one of the last scalar value, U+10FFFF; the smallest Guid has
//   every bit clear, the largest every bit set;
// - for each other family an index part can end at and the engine stores, Boolean, Float32, Float64,
//   Decimal, DateTimeOffset, DateOnly, TimeOnly, TimeSpan, an Enum and a Flags Enum, an entity of Item is
//   found by the index on its field in every form, and the entity read by its key holds the value. The
//   smallest Float32 and Float64 are MinValue, the largest MaxValue (NaN and the infinities are the
//   business of FloatingPointSearchModelTests); the Enum is Level, from -300 to 300, the Flags type Rights, from
//   no bit to every declared bit.
// IndexPartValueTests holds every scalar type found by its value and by null, through a class and by
// name; KeyTypeTests every key type read by its key and by an integer of another width.
public sealed class OneValueEveryWayTests
{
    public static IEnumerable<TheoryDataRow<string>> KeyFamilies =>
    [
        new("Int8"), new("UInt8"), new("Int16"), new("UInt16"), new("Int32"), new("UInt32"), new("Int64"), new("UInt64"),
        new("String"), new("Guid"),
    ];

    public static IEnumerable<TheoryDataRow<string>> OtherFamilies =>
    [
        new("Boolean"), new("Float32"), new("Float64"), new("Decimal"), new("DateTimeOffset"), new("DateOnly"),
        new("TimeOnly"), new("TimeSpan"), new("Level"), new("Rights"),
    ];

    [Theory]
    [MemberData(nameof(KeyFamilies))]
    public async Task Value_of_a_key_family_is_found_by_its_key_and_by_an_index_through_a_class_and_by_name_at_both_ends_of_its_type(string family)
    {
        await (family switch
        {
            "Int8" => ExpectKeyFamilyAsync(family, sbyte.MinValue, sbyte.MaxValue),
            "UInt8" => ExpectKeyFamilyAsync(family, byte.MinValue, byte.MaxValue),
            "Int16" => ExpectKeyFamilyAsync(family, short.MinValue, short.MaxValue),
            "UInt16" => ExpectKeyFamilyAsync(family, ushort.MinValue, ushort.MaxValue),
            "Int32" => ExpectKeyFamilyAsync(family, int.MinValue, int.MaxValue),
            "UInt32" => ExpectKeyFamilyAsync(family, uint.MinValue, uint.MaxValue),
            "Int64" => ExpectKeyFamilyAsync(family, long.MinValue, long.MaxValue),
            "UInt64" => ExpectKeyFamilyAsync(family, ulong.MinValue, ulong.MaxValue),
            "String" => ExpectKeyFamilyAsync(family, "", "\U0010FFFF"),
            _ => ExpectKeyFamilyAsync(family, Guid.Empty, Guid.AllBitsSet),
        });
    }

    [Theory]
    [MemberData(nameof(OtherFamilies))]
    public async Task Value_of_every_other_family_is_found_by_an_index_through_a_class_and_by_name_at_both_ends_of_its_type(string family)
    {
        var ends = BindingSchema.EndsOf(family);

        await using var database = await BindingSchema.OpenAsync();
        var stored = BindingSearch.Numbered(ends[0], ends[1], null);

        await BindingSearch.InsertAsync(database, family, stored);

        foreach (var entity in stored.Take(2))
        {
            await BindingSearch.ExpectAsync(database, family, TypedValues.Equal(entity.Value!), reverse: false, [entity], $"By{family} = {entity.Value}");

            Assert.Equal(entity.Value, database.Set<Item>().Get(entity.Id)!.ValueOf(family));
            Assert.Equal(entity.Value, database.Set("Item").Get(entity.Id)![family]);
        }
    }

    // The smallest value inserted through the class, the largest by name, each as its key and its Value; each
    // read by its key and found by its Value in every form.
    private static async Task ExpectKeyFamilyAsync<TKey>(string family, TKey smallest, TKey largest)
        where TKey : notnull
    {
        await using var database = await TestData.OpenMasterAsync(schema: BindingSchema.KeyedBy<TKey>(family));
        var keyed = database.Set<Keyed<TKey>>();
        var records = database.Set("Keyed");

        await keyed.InsertAsync(new Keyed<TKey> { Id = smallest, Value = smallest });
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = largest, ["Value"] = largest });

        foreach (var value in new[] { smallest, largest })
        {
            var read = keyed.Get(value);
            var readByName = records.Get(value);

            Assert.NotNull(read);
            Assert.NotNull(readByName);
            Assert.Equal(value, read.Value);
            Assert.Equal((object)value, readByName["Value"]);

            var byClass = keyed.Index("ByValue");
            var byName = records.Index("ByValue");
            var key = IndexKey.Equal(value);

            Assert.Equal(value, Assert.Single(byClass.Find(key)).Id);
            Assert.Equal(value, Assert.Single((await byClass.FindAsync(key, new LatticeFindOptions())).Items).Id);
            Assert.Equal((object)value, Assert.Single(byName.Find(key))["Id"]);
            Assert.Equal((object)value, Assert.Single((await byName.FindAsync(key, new LatticeFindOptions())).Items)["Id"]);
        }
    }
}
