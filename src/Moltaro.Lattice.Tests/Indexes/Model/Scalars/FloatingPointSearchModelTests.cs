using System.Numerics;

namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// A search of an index of one Float32 or Float64 part agrees with the model of the order, the field
// nullable and not (docs/decisions/queries.md, "Floating-point special values": NaN values are one key and
// come first, then the negative infinity, the finite values and the positive infinity; the two zeros are one
// key, and each entity keeps the bits it was stored with). A cell stores 400 entities over at least five
// leaves: NaN in three forms of bits, the negative infinity, both zeros, the smallest subnormals and the
// ends of the finite values among them, and random bits, which hold NaN payloads of their own; the
// positive infinity where the field is never null, and where it is nullable as a bound beyond what is
// stored (ScalarTypes). It searches as every cell does (IntegerSearchModelTests). A separate nullable cell
// for each precision stores all the special values together, including the positive infinity and a null.
// Its exact searches and one-sided bounds give both signs of zero separately because the ordinary cells
// choose just one representative per key.
public sealed class FloatingPointSearchModelTests
{
    public static IEnumerable<TheoryDataRow<string, bool>> Cells =>
    [
        new("Float32", true), new("Float32", false), new("Float64", true), new("Float64", false),
    ];

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_a_floating_point_index_by_value_by_range_and_in_pages_agrees_with_the_model_in_both_directions_on_a_master_and_a_child(string type, bool nullable) =>
        ScalarTypes.Named(type).RunAsync(nullable);

    [Theory]
    [InlineData("Float32")]
    [InlineData("Float64")]
    public Task Floating_point_special_values_match_the_model_in_exact_searches_as_bounds_and_in_the_whole_index(string type) =>
        type switch
        {
            "Float32" => SearchSpecialValuesAsync<float>(ScalarTypes.Float32),
            "Float64" => SearchSpecialValuesAsync<double>(ScalarTypes.Float64),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    private static Task SearchSpecialValuesAsync<T>(ScalarType type)
        where T : struct, IFloatingPointIeee754<T>
    {
        var cell = new ScalarCell(type, Nullable: true);
        var model = new IndexModel(type.Order);
        object?[] values = [.. type.Edges, T.NegativeZero, T.Zero, null];
        List<ModelEntry> given = [.. values.Select((value, place) => new ModelEntry(place + 1, [value]))];
        List<ModelSearch> searches = [.. ModelSearches.BothWays([], null, null)];
        object[] specials = [T.NaN, T.NegativeInfinity, T.PositiveInfinity, T.Zero, T.NegativeZero];

        foreach (var value in specials)
        {
            var exact = new ModelSearch([value], null, null, Reverse: false);

            Assert.NotEmpty(model.Answer(given, exact));
            searches.Add(exact);
            searches.Add(exact with { Reverse = true });

            foreach (var (from, to) in ModelSearches.OneSidedRanges(value))
            {
                searches.AddRange(ModelSearches.BothWays([], from, to));
            }
        }

        return ModelRun.RunAsync(
            $"{type.Name} special values",
            ScalarSchema.Of(cell, typeof(Sample<T?>)),
            ScalarRun.TableOf<T?>(model),
            given,
            searches,
            []);
    }
}
