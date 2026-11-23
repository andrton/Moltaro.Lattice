namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// A search of an index of one Decimal part agrees with the model of the order, the field nullable and not
// (docs/design/ordered-index.md, "Entries and their order": a Decimal by its value, so 1.0 and 1.00 are one
// key; each entity keeps the scale it was stored with). A cell stores 400 entities over at least ten leaves,
// the ends of the type, zeros of both signs and one value in three scales among them (ScalarTypes); it
// searches as every cell does (IntegerSearchModelTests).
public sealed class DecimalSearchModelTests
{
    public static IEnumerable<TheoryDataRow<bool>> Cells => [new(true), new(false)];

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_a_Decimal_index_by_value_by_range_and_in_pages_agrees_with_the_model_in_both_directions_on_a_master_and_a_child(bool nullable) =>
        ScalarTypes.Decimal.RunAsync(nullable);
}
