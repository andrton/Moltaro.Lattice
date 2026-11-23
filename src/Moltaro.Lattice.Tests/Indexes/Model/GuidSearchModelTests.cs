namespace Moltaro.Lattice.Tests.Indexes.Model;

// A search of an index of one Guid part agrees with the model of the order, the field nullable and not
// (docs/design/ordered-index.md, "Entries and their order": a Guid by CompareTo, field by field). A cell
// stores 400 entities over at least ten leaves: the empty Guid and the one of every bit, Guids that differ
// only in their first, a middle or the last field, and random ones (ScalarTypes); it searches as every cell
// does (IntegerSearchModelTests).
public sealed class GuidSearchModelTests
{
    public static IEnumerable<TheoryDataRow<bool>> Cells => [new(true), new(false)];

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_a_Guid_index_by_value_by_range_and_in_pages_agrees_with_the_model_in_both_directions_on_a_master_and_a_child(bool nullable) =>
        ScalarTypes.Guid.RunAsync(nullable);
}
