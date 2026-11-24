namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// A search of an index of one Float32 or Float64 part agrees with the model of the order, the field
// nullable and not (docs/decisions/queries.md, "Floating-point special values": NaN values are one key and
// come first, then the negative infinity, the finite values and the positive infinity; the two zeros are one
// key, and each entity keeps the bits it was stored with). A cell stores 400 entities over at least five
// leaves: NaN in three forms of bits, the negative infinity, both zeros, the smallest subnormals and the
// ends of the finite values among them, and random bits, which hold NaN payloads of their own; the
// positive infinity where the field is never null, and where it is nullable as a bound beyond what is
// stored (ScalarTypes). It searches as every cell does (IntegerSearchModelTests).
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
}
