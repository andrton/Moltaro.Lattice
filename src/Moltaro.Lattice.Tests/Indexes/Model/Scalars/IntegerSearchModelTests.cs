namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// A search of an index of one integer part agrees with the model of the order, for every width signed and
// unsigned, the field nullable and not (docs/design/ordered-index.md, "Entries and their order": an
// integer by its value, signed or unsigned at its width; a null after every value). A cell stores 400
// entities over at least four leaves, with the ends of the type, zero, one, minus one and the sign and the
// high bits among them (ScalarTypes), and searches every value it stored and values it did not, ranges with
// every kind of bound on stored values, between them and beyond both ends, both directions and pages, through
// the class and by name, on a master and on a child (ModelRun, ModelSearches).
// An Int64 that is never null is held by OneNumberSearchModelTests, under a name of its own.
public sealed class IntegerSearchModelTests
{
    public static IEnumerable<TheoryDataRow<string, bool>> Cells =>
    [
        new("Int8", true), new("Int8", false), new("UInt8", true), new("UInt8", false),
        new("Int16", true), new("Int16", false), new("UInt16", true), new("UInt16", false),
        new("Int32", true), new("Int32", false), new("UInt32", true), new("UInt32", false),
        new("Int64", true), new("UInt64", true), new("UInt64", false),
    ];

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_an_integer_index_by_value_by_range_and_in_pages_agrees_with_the_model_in_both_directions_on_a_master_and_a_child(string type, bool nullable) =>
        ScalarTypes.Named(type).RunAsync(nullable);
}
