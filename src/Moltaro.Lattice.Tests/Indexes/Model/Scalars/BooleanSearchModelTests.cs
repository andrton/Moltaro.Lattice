namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// A search of an index of one Boolean part agrees with the model of the order, the field nullable and not
// (docs/design/ordered-index.md, "Entries and their order": false before true, a null after both). A cell
// stores 400 entities over at least four leaves, runs of one key that span leaves; a Boolean has no value
// beyond false and true and none between, so every bound is a stored value and no search is of a value that
// is not stored. It searches as every cell does (IntegerSearchModelTests).
public sealed class BooleanSearchModelTests
{
    public static IEnumerable<TheoryDataRow<bool>> Cells => [new(true), new(false)];

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_a_Boolean_index_by_value_by_range_and_in_pages_agrees_with_the_model_in_both_directions_on_a_master_and_a_child(bool nullable) =>
        ScalarTypes.Boolean.RunAsync(nullable);
}
