namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A range over a part of every scalar family finds what the oracle finds, in both directions, through the
// class and by name (docs/design/ordered-index.md, "Entries and their order": a part is ordered by the
// comparison of its type, never as raw bytes):
// - for each family OrderAndBoundsTests does not hold, Boolean, the six integers other than Int32 and
//   Int64, Float32, Float64, Decimal, Guid, DateTimeOffset, DateOnly, TimeOnly and TimeSpan: five values
//   from the smallest to the largest of the type, where the order of the bytes of a value differs from the
//   order of the values, and an entity without a value; ranges inclusive and exclusive at each end, either
//   end open, with the smallest and the largest value and a value between as a bound;
// - a bound given in another type of the allowlist, an integer for a Decimal and a Float32 for a Float64,
//   finds what the same bound in the type of the field finds.
// OrderAndBoundsTests holds Int32, Int64 and String, in composite keys; FloatingPointKeyTests NaN, the
// infinities and the two zeros as bounds; EnumAndFlagsTests the Enum and Flags parts.
public sealed class RangeOfEveryFamilyTests
{
    public static IEnumerable<TheoryDataRow<string>> Families =>
    [
        new("Boolean"), new("Int8"), new("UInt8"), new("Int16"), new("UInt16"), new("UInt32"), new("UInt64"),
        new("Float32"), new("Float64"), new("Decimal"), new("Guid"), new("DateTimeOffset"), new("DateOnly"),
        new("TimeOnly"), new("TimeSpan"),
    ];

    [Theory]
    [MemberData(nameof(Families))]
    public async Task Range_with_inclusive_and_exclusive_bounds_at_the_ends_of_the_type_finds_the_values_of_the_oracle_in_both_directions(string family)
    {
        var ordered = ValuesOf(family);

        // Stored out of their order, with an entity without a value among them.
        var stored = BindingSearch.Numbered([ordered[2], ordered[4], null, ordered[0], ordered[3], ordered[1]]);
        var smallest = ordered[0];
        var between = ordered[2];
        var largest = ordered[^1];

        await using var database = await BindingSchema.OpenAsync();
        await BindingSearch.InsertAsync(database, family, stored);

        (RangeEnd? From, RangeEnd? To)[] ranges =
        [
            (RangeEnd.Including(smallest), RangeEnd.Including(largest)),
            (RangeEnd.Excluding(smallest), RangeEnd.Excluding(largest)),
            (RangeEnd.Including(between), null),
            (RangeEnd.Excluding(between), null),
            (null, RangeEnd.Including(between)),
            (null, RangeEnd.Excluding(between)),
            (RangeEnd.Including(largest), null),
            (RangeEnd.Excluding(largest), null),
            (null, RangeEnd.Including(smallest)),
            (null, RangeEnd.Excluding(smallest)),
            (RangeEnd.Excluding(smallest), RangeEnd.Including(between)),
        ];

        foreach (var (from, to) in ranges)
        {
            await BindingSearch.ExpectRangeAsync(database, family, stored, from, to);
        }
    }

    [Fact]
    public async Task Bound_given_in_another_type_of_the_allowlist_finds_what_the_bound_in_the_type_of_the_field_finds()
    {
        await using var database = await BindingSchema.OpenAsync();
        var decimals = BindingSearch.Numbered(-2.5m, -2m, 0m, 1.00m, 3m, 3.5m);
        List<Stored> doubles = [new(11, -1.5), new(12, 0.5), new(13, 0.75), new(14, 1.5), new(15, 2.5)];

        await BindingSearch.InsertAsync(database, "Decimal", decimals);
        await BindingSearch.InsertAsync(database, "Float64", doubles);

        foreach (var reverse in new[] { false, true })
        {
            await BindingSearch.ExpectAsync(
                database,
                "Decimal",
                IndexKey.All.Range(IndexBound.Exclusive(-2), IndexBound.Inclusive(3L)),
                reverse,
                ValueOracle.Range(decimals, RangeEnd.Excluding(-2m), RangeEnd.Including(3m), reverse),
                "ByDecimal from an exclusive Int32 -2 to an inclusive Int64 3");

            await BindingSearch.ExpectAsync(
                database,
                "Float64",
                IndexKey.All.Range(IndexBound.Inclusive(0.5f), IndexBound.Exclusive(1.5f)),
                reverse,
                ValueOracle.Range(doubles, RangeEnd.Including(0.5), RangeEnd.Excluding(1.5), reverse),
                "ByFloat64 from an inclusive Float32 0.5 to an exclusive Float32 1.5");
        }
    }

    // Five values of the type of the field, in their order: the smallest of the type, three between with
    // values where the bytes order otherwise (a sign, the high bit of an unsigned value, the scale of a
    // Decimal, the parts of a Guid), and the largest of the type.
    private static object[] ValuesOf(string family)
    {
        var ends = BindingSchema.EndsOf(family);

        return [ends[0], .. Between(family), ends[1]];
    }

    private static object[] Between(string family) => family switch
    {
        "Boolean" => [false, false, true],
        "Int8" => [(sbyte)-1, (sbyte)0, (sbyte)1],
        "UInt8" => [(byte)1, (byte)0x7f, (byte)0x80],
        "Int16" => [(short)-1, (short)0, (short)256],
        "UInt16" => [(ushort)1, (ushort)0x7fff, (ushort)0x8000],
        "UInt32" => [1u, 0x7fff_ffffu, 0x8000_0000u],
        "UInt64" => [1ul, (ulong)long.MaxValue, (ulong)long.MaxValue + 1],
        "Float32" => [-1.5f, 0f, 2f],
        "Float64" => [-1.5, 0d, 2d],
        "Decimal" => [-0.5m, 1.00m, 1.5m],
        "Guid" =>
        [
            Guid.Parse("00000001-ffff-ffff-ffff-ffffffffffff"), Guid.Parse("7fffffff-0000-0000-0000-000000000001"),
            Guid.Parse("80000000-0000-0000-0000-000000000000"),
        ],
        "DateTimeOffset" => [DateTimeOffset.UnixEpoch.AddTicks(-1), DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero)],
        "DateOnly" => [new DateOnly(1969, 12, 31), new DateOnly(1970, 1, 1), new DateOnly(2026, 11, 21)],
        "TimeOnly" => [new TimeOnly(1), new TimeOnly(11, 59), new TimeOnly(12, 0)],
        _ => [TimeSpan.FromTicks(-1), TimeSpan.Zero, TimeSpan.FromDays(1)],
    };
}
