using Moltaro.Lattice.TestSupport.PartialUpdates;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A value of a key and of a bound is bound to the type of its field by the accepted allowlist, at its
// boundaries (docs/design/milestone-1-engine-contract.md, "Index preparation and traversal";
// docs/decisions/queries.md, Q07.3.6, "Numeric query inputs"; IndexKey and IndexBound): the same number
// in the type of the field, or nothing.
// - Each row names a field, a value given as its own .NET type, and the value of the field type it binds
//   to, or that it is refused. A value that binds is proven by what it finds: the search with it as the
//   value of Equal, as an inclusive and as an exclusive lower bound and upper bound, returns the entities
//   of the oracle over the stored values, which are the bound value, the smallest and the largest value
//   of the field type, and the values a wrong binding would find instead, such as 0.1 beside the Float32
//   0.1. A value that is refused is the ArgumentException of a key the index cannot serve in all five
//   forms, through the class and by name, by Find and by FindAsync.
// - The rows: an integer one under and one over each end of an integer type; any integer for a Decimal;
//   for a Float32 and a Float64 an integer only where the type holds it exactly, around 2^24 and 2^53 and
//   at the ends of Int64 and UInt64; a Float32 for a Float64, NaN, the infinities and -0 kept; a Float64
//   for a Float32 only where it converts back to itself, an overflow and a subnormal Float64 refused, not
//   bound to an infinity or a zero; and refused, a Decimal for a floating-point field and the reverse, a
//   number with a fraction type for an integer field also when it is whole, a value that is not a number
//   for a numeric field and a number for a field that is not numeric, and a DateTime for a DateTimeOffset.
// IndexKeyArgumentTests holds a Double, a Guid and an out-of-range Int64 for an Int32 field and an integer
// for a String field; IndexPartTypeTests an integer of another width for an Int16 inside an owned value.
public sealed class AllowlistTests
{
    private static readonly string[] IntegerFields = ["Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64"];

    private static readonly Dictionary<string, AllowlistCase> Cases = Rows().ToDictionary(row => row.Name);

    public static IEnumerable<TheoryDataRow<string>> Names =>
        Cases.Values.Select(row => new TheoryDataRow<string>(row.Name));

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Value_of_a_key_or_a_bound_is_bound_to_the_same_number_in_the_type_of_its_field_or_refused(string name)
    {
        var row = Cases[name];
        var ends = BindingSchema.EndsOf(row.Field);
        object?[] values = row.Bound is null ? ends : [row.Bound, .. ends, .. row.AlsoStored];
        var stored = BindingSearch.Numbered(values);

        await using var database = await BindingSchema.OpenAsync();
        await BindingSearch.InsertAsync(database, row.Field, stored);

        var index = $"By{row.Field}";

        if (row.Bound is null)
        {
            await BindingSearch.ExpectRefusedAsync(database, index, TypedValues.Equal(row.Given), $"{name}, as the value");

            foreach (var inclusive in new[] { true, false })
            {
                await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.All.Range(TypedValues.Bound(row.Given, inclusive), null), $"{name}, as a lower bound");
                await BindingSearch.ExpectRefusedAsync(database, index, IndexKey.All.Range(null, TypedValues.Bound(row.Given, inclusive)), $"{name}, as an upper bound");
            }

            return;
        }

        await BindingSearch.ExpectAsync(database, row.Field, TypedValues.Equal(row.Given), reverse: false, BindingSearch.Equal(stored, row.Bound), $"{name}, as the value");

        foreach (var inclusive in new[] { true, false })
        {
            var asLower = IndexKey.All.Range(TypedValues.Bound(row.Given, inclusive), null);
            var asUpper = IndexKey.All.Range(null, TypedValues.Bound(row.Given, inclusive));
            var end = new RangeEnd(row.Bound, inclusive);

            await BindingSearch.ExpectAsync(database, row.Field, asLower, reverse: false, BindingSearch.Range(stored, end, null, reverse: false), $"{name}, as a lower bound {end}");
            await BindingSearch.ExpectAsync(database, row.Field, asUpper, reverse: false, BindingSearch.Range(stored, null, end, reverse: false), $"{name}, as an upper bound {end}");
        }
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [MemberData(nameof(Names))]
    public async Task Scalar_assignments_use_the_same_allowlist_as_index_queries_by_class_and_by_name(string name)
    {
        var row = Cases[name];
        await using var database = await BindingSchema.OpenAsync();
        var typed = database.Set<Item>();
        var named = database.Set("Item");
        await BindingSearch.InsertAsync(database, row.Field, BindingSearch.Numbered(null, null));
        var byName = named.CreateUpdate(1L);
        var byClass = typed.CreateUpdate(2L);

        if (row.Bound is null)
        {
            Assert.Throws<ArgumentException>(() => byName.Assign(row.Field, row.Given));
            Assert.Throws<ArgumentException>(() => byClass.Assign(row.Field, row.Given));
            Assert.Equal(0, byName.Count);
            Assert.Equal(0, byClass.Count);
            return;
        }

        var first = byName.Assign(row.Field, row.Given);
        var second = byClass.Assign(row.Field, row.Given);
        await byName.CommitAsync();
        await byClass.CommitAsync();
        Assert.True(first.Changed && second.Changed);
        var search = TypedValues.Equal(row.Given);
        var expected = BindingSearch.Numbered(row.Bound, row.Bound);
        await BindingSearch.ExpectAsync(database, row.Field, search, reverse: false, expected, name);
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task An_undeclared_enum_value_passes_shape_check_but_fails_submission()
    {
        await using var database = await BindingSchema.OpenAsync();
        var typed = database.Set<Item>();
        await typed.InsertAsync(Item.Of(1, "Level", (short)-2));
        var update = typed.CreateUpdate(1L);
        var command = update.Assign("Level", (short)-3);
        var commit = update.CommitAsync();

        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => commit);
        Assert.Throws<InvalidOperationException>(() => command.Changed);
        Assert.Single(typed.Index("ByLevel").Find(IndexKey.Equal((short)-2)));
    }

    private static IEnumerable<AllowlistCase> Rows()
    {
        // Integer to integer: one under and one over each end of the target.
        yield return AllowlistCase.Refused("UInt8", -1);
        yield return AllowlistCase.Binds("UInt8", 0, (byte)0);
        yield return AllowlistCase.Binds("UInt8", 255, (byte)255);
        yield return AllowlistCase.Refused("UInt8", 256);
        yield return AllowlistCase.Binds("Int64", (ulong)long.MaxValue, long.MaxValue);
        yield return AllowlistCase.Refused("Int64", (ulong)long.MaxValue + 1);
        yield return AllowlistCase.Refused("UInt64", -1L);
        yield return AllowlistCase.Refused("Int8", -129);
        yield return AllowlistCase.Binds("Int8", -128, (sbyte)-128);
        yield return AllowlistCase.Binds("Int8", 127, (sbyte)127);
        yield return AllowlistCase.Refused("Int8", 128);

        // Any integer to Decimal, exactly.
        yield return AllowlistCase.Binds("Decimal", long.MinValue, (decimal)long.MinValue);
        yield return AllowlistCase.Binds("Decimal", ulong.MaxValue, (decimal)ulong.MaxValue);

        // An integer to Float32 only where Float32 holds it exactly: 2^24, 2^24 + 1 and 2^24 + 2, and the
        // ends of Int64 and UInt64, of which only -2^63 is a Float32.
        yield return AllowlistCase.Binds("Float32", 16_777_216, 16_777_216f, 16_777_218f);
        yield return AllowlistCase.Refused("Float32", 16_777_217);
        yield return AllowlistCase.Binds("Float32", 16_777_218, 16_777_218f, 16_777_216f);
        yield return AllowlistCase.Binds("Float32", long.MinValue, (float)long.MinValue);
        yield return AllowlistCase.Refused("Float32", long.MaxValue);
        yield return AllowlistCase.Refused("Float32", ulong.MaxValue);

        // An integer to Float64 likewise, around 2^53 (the cases of Q07.3.6).
        yield return AllowlistCase.Binds("Float64", 9_007_199_254_740_992L, 9_007_199_254_740_992d, 9_007_199_254_740_994d);
        yield return AllowlistCase.Refused("Float64", 9_007_199_254_740_993L);
        yield return AllowlistCase.Binds("Float64", 9_007_199_254_740_994L, 9_007_199_254_740_994d, 9_007_199_254_740_992d);

        // Float32 to Float64: the same number, which 0.1f is and 0.1 is not; NaN, the infinities and -0.
        yield return AllowlistCase.Binds("Float64", 0.1f, (double)0.1f, 0.1);
        yield return AllowlistCase.Binds("Float64", float.NaN, double.NaN);
        yield return AllowlistCase.Binds("Float64", float.PositiveInfinity, double.PositiveInfinity);
        yield return AllowlistCase.Binds("Float64", float.NegativeInfinity, double.NegativeInfinity);
        yield return AllowlistCase.Binds("Float64", -0f, -0d, 0d);

        // Float64 to Float32 only where the value converts back to itself; an overflow is not an infinity
        // and a subnormal Float64 is not a zero.
        yield return AllowlistCase.Binds("Float32", 0.5, 0.5f);
        yield return AllowlistCase.Refused("Float32", 0.1);
        yield return AllowlistCase.Binds("Float32", (double)float.MaxValue, float.MaxValue);
        yield return AllowlistCase.Refused("Float32", 3.5e38);
        yield return AllowlistCase.Refused("Float32", double.Epsilon);
        yield return AllowlistCase.Binds("Float32", double.NaN, float.NaN);
        yield return AllowlistCase.Binds("Float32", double.PositiveInfinity, float.PositiveInfinity);
        yield return AllowlistCase.Binds("Float32", double.NegativeInfinity, float.NegativeInfinity);
        yield return AllowlistCase.Binds("Float32", -0d, -0f, 0f);

        // Decimal and floating point are not converted into each other.
        foreach (var field in new[] { "Float32", "Float64" })
        {
            yield return AllowlistCase.Refused(field, 0.5m);
            yield return AllowlistCase.Refused(field, 42m);
        }

        yield return AllowlistCase.Refused("Decimal", 0.5f);
        yield return AllowlistCase.Refused("Decimal", 0.5);

        // A number of a fraction type for an integer field, also when it is whole.
        foreach (var field in IntegerFields)
        {
            yield return AllowlistCase.Refused(field, 42.0);
            yield return AllowlistCase.Refused(field, 42f);
            yield return AllowlistCase.Refused(field, 42.0m);
        }

        // A value that is not a number for a numeric field, and a number for a field that is not numeric.
        object[] notNumbers =
        [
            true, "42", Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e"), new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero),
            new DateOnly(2026, 11, 21), new TimeOnly(8, 0), TimeSpan.FromHours(8),
        ];

        foreach (var field in new[] { "Int32", "Float64", "Decimal" })
        {
            foreach (var value in notNumbers)
            {
                yield return AllowlistCase.Refused(field, value);
            }
        }

        foreach (var field in new[] { "Boolean", "Exact", "Guid", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan" })
        {
            yield return AllowlistCase.Refused(field, 42);
        }

        yield return AllowlistCase.Refused("DateTimeOffset", new DateTime(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc));
    }
}
