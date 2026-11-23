namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// NaN, the infinities and the two zeros of a Float32 and a Float64 as the value of a key and as a bound,
// against an oracle ordered by CompareTo (docs/decisions/queries.md, "Floating-point special values": NaN
// values are one key and come first, then the negative infinity, the finite values, the positive
// infinity; the two zeros are one key and each keeps its sign):
// - an exact search of NaN finds every entity that holds a NaN, whatever its bits; of 0 and of -0 each
//   finds both zeros, and every entity is returned with the sign it was stored with;
// - the whole index in both directions is the order of the oracle, and its exact reverse;
// - NaN, each infinity and -0 as an inclusive and as an exclusive bound, lower and upper, in both
//   directions, find what the oracle finds.
// Each search runs through the class and by name; the stored values are those of Values, with the values
// given in the type of the field.
public sealed class FloatingPointKeyTests
{
    public static IEnumerable<TheoryDataRow<string>> Fields => [new("Float32"), new("Float64")];

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Exact_search_of_NaN_finds_every_NaN_and_of_either_zero_finds_both_zeros_each_with_its_own_sign(string field)
    {
        await using var database = await BindingSchema.OpenAsync();
        var stored = BindingSearch.Numbered(Values(field));

        await BindingSearch.InsertAsync(database, field, stored);

        foreach (var value in Specials(field))
        {
            var expected = BindingSearch.Equal(stored, value);

            Assert.NotEmpty(expected);

            await BindingSearch.ExpectAsync(database, field, TypedValues.Equal(value), reverse: false, expected, $"By{field} = {new Stored(0, value)}");
        }
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task Whole_index_of_floating_point_values_is_in_the_order_of_CompareTo_and_in_its_exact_reverse(string field)
    {
        await using var database = await BindingSchema.OpenAsync();
        var stored = BindingSearch.Numbered(Values(field));

        await BindingSearch.InsertAsync(database, field, stored);

        await BindingSearch.ExpectRangeAsync(database, field, stored, from: null, to: null);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public async Task NaN_an_infinity_and_negative_zero_as_an_inclusive_or_exclusive_bound_find_what_CompareTo_orders_on_their_side(string field)
    {
        await using var database = await BindingSchema.OpenAsync();
        var stored = BindingSearch.Numbered(Values(field));

        await BindingSearch.InsertAsync(database, field, stored);

        foreach (var value in Specials(field))
        {
            foreach (var inclusive in new[] { true, false })
            {
                var end = new RangeEnd(value, inclusive);

                await BindingSearch.ExpectRangeAsync(database, field, stored, end, null);
                await BindingSearch.ExpectRangeAsync(database, field, stored, null, end);
            }
        }
    }

    // NaN, the two infinities, 0 and -0, in the type of the field.
    private static object[] Specials(string field) => field == "Float32"
        ? [float.NaN, float.NegativeInfinity, float.PositiveInfinity, 0f, -0f]
        : [double.NaN, double.NegativeInfinity, double.PositiveInfinity, 0d, -0d];

    // The stored values: NaN in three forms of bits, with the sign and with another payload; both
    // infinities; the ends of the finite values; the smallest subnormal on both sides of the zeros; both
    // zeros twice; one and minus one; and an entity without a value.
    private static object?[] Values(string field) => field == "Float32"
        ?
        [
            float.NaN, -float.NaN, BitConverter.Int32BitsToSingle(0x7fc0_0001), float.NegativeInfinity, float.MinValue,
            -1f, -float.Epsilon, -0f, 0f, -0f, 0f, float.Epsilon, 1f, float.MaxValue, float.PositiveInfinity, null,
        ]
        :
        [
            double.NaN, -double.NaN, BitConverter.Int64BitsToDouble(0x7ff8_0000_0000_0001), double.NegativeInfinity, double.MinValue,
            -1d, -double.Epsilon, -0d, 0d, -0d, 0d, double.Epsilon, 1d, double.MaxValue, double.PositiveInfinity, null,
        ];
}
