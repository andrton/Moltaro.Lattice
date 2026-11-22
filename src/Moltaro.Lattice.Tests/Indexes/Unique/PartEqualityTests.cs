namespace Moltaro.Lattice.Tests.Indexes.Unique;

// Two keys of a unique index are one value when the comparison of every part holds them equal, the one
// comparison an index orders by (decision on unique indexes, "Equality follows every component's schema
// comparator, including case policy, floating-point NaN/zero rules and enum numeric values. Empty text is
// a value, not null"): a text compared Ordinal by its code units, one compared without case by the case
// table, every NaN one value and the two zeros one value, an Enum by its number, and the empty text a
// value one entity holds. Each test stores one entity with a value, refuses a second one with a value
// the comparison holds equal, naming the index, and accepts one with a value it holds different. Valued
// of UniqueSchemas, in a UTF-8 and a UTF-16 database.
public sealed class PartEqualityTests
{
    public static IEnumerable<TheoryDataRow<string>> Encodings => [new("Utf8"), new("Utf16")];

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Text_compared_Ordinal_is_one_value_in_the_same_code_units_and_another_in_another_case(string encoding)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Valued(encoding));

        await ExpectOneValueAsync(database, first: 100, "Exact", "Ada", taken: ["Ada"], free: ["ada", "ADA", "Ada "]);
        await ExpectOneValueAsync(database, first: 200, "Exact", "École", taken: ["École"], free: ["école", "ÉCOLE"]);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Text_compared_without_case_is_one_value_in_every_case_of_it(string encoding)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Valued(encoding));

        await ExpectOneValueAsync(database, first: 100, "Folded", "Ada", taken: ["ADA", "ada", "aDa"], free: ["Adam", "Ad"]);
        await ExpectOneValueAsync(database, first: 200, "Folded", "École", taken: ["ÉCOLE", "école"], free: ["Ecole"]);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Empty_text_is_a_value_that_one_entity_holds_and_not_a_null(string encoding)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Valued(encoding));

        await ExpectOneValueAsync(database, first: 100, "Exact", "", taken: [""], free: [" "]);
        await ExpectOneValueAsync(database, first: 200, "Folded", "", taken: [""], free: [" "]);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Every_NaN_is_one_value_and_the_two_zeros_are_one_value_of_a_Float64(string encoding)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Valued(encoding));
        var otherNaN = BitConverter.Int64BitsToDouble(unchecked((long)0xFFF8_0000_0000_0001));

        Assert.True(double.IsNaN(otherNaN) && BitConverter.DoubleToInt64Bits(otherNaN) != BitConverter.DoubleToInt64Bits(double.NaN), "The premise: a NaN of other bits.");

        await ExpectOneValueAsync(database, first: 100, "Real", double.NaN, taken: [otherNaN, double.NaN], free: [double.PositiveInfinity]);
        await ExpectOneValueAsync(database, first: 200, "Real", 0.0, taken: [-0.0, 0.0], free: [double.Epsilon]);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Every_NaN_is_one_value_and_the_two_zeros_are_one_value_of_a_Float32(string encoding)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Valued(encoding));
        var otherNaN = BitConverter.Int32BitsToSingle(unchecked((int)0xFFC0_0001));

        Assert.True(float.IsNaN(otherNaN) && BitConverter.SingleToInt32Bits(otherNaN) != BitConverter.SingleToInt32Bits(float.NaN), "The premise: a NaN of other bits.");

        await ExpectOneValueAsync(database, first: 100, "Single", float.NaN, taken: [otherNaN, float.NaN], free: [float.NegativeInfinity]);
        await ExpectOneValueAsync(database, first: 200, "Single", -0.0f, taken: [0.0f, -0.0f], free: [float.Epsilon]);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Enum_part_is_one_value_by_its_number(string encoding)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Valued(encoding));

        // A record holds an Enum as the integer of its underlying type.
        await ExpectOneValueAsync(database, first: 100, "Level", (short)5, taken: [(short)5], free: [(short)-2]);
    }

    // Stores an entity with the value in the field, refuses an insert and a replace of another entity for
    // each value taken, naming the index of the field, accepts an insert for each value free, and leaves the
    // index with one owner of the value. The ids of the entities of a call count from the first given, so
    // that the calls of one test give distinct ones.
    private static async Task ExpectOneValueAsync(ILatticeDatabase database, long first, string field, object value, object[] taken, object[] free)
    {
        var values = database.Set("Valued");
        var index = $"By{field}";
        var owner = first;
        var other = first + 1;
        var next = first + 2;

        await values.InsertAsync(UniqueSchemas.RecordOf(values, owner, (field, value)));
        await values.InsertAsync(UniqueSchemas.RecordOf(values, other));

        foreach (var equal in taken)
        {
            await UniqueSchemas.ExpectTakenAsync(index, () => values.InsertAsync(UniqueSchemas.RecordOf(values, next++, (field, equal))));
            await UniqueSchemas.ExpectTakenAsync(index, () => values.ReplaceAsync(UniqueSchemas.RecordOf(values, other, (field, equal))));
        }

        foreach (var different in free)
        {
            var id = next++;

            await values.InsertAsync(UniqueSchemas.RecordOf(values, id, (field, different)));
            Assert.Equal(different, values.Get(id)![field]);
        }

        Assert.Null(values.Get(other)![field]);
        Assert.Equal([owner], UniqueSchemas.IdsOf(values.Index(index).Find(IndexKey.Equal(value))));
    }
}
