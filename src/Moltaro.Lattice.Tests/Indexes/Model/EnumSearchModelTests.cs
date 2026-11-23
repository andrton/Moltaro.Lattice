namespace Moltaro.Lattice.Tests.Indexes.Model;

// A search of an index of one Enum or Flags part agrees with the model of the order, the field nullable and
// not (LatticeEnumType: values compare as numbers of the underlying type, with its sign; IndexKey: a value
// is the integer, and only one its enum type allows). A cell stores 400 entities over at least four leaves,
// drawn from the members of its enum type, which lie on both sides of zero and at the ends of the
// underlying type: Grade over Int16, Span over Int64, Wide over UInt64 with members on both sides of the
// largest Int64, and Rights, Flags over Int32, any combination of its bits, the sign bit among them
// (ModelSchema, ScalarTypes). The member Unused of each is never stored, so that a search of it finds
// nothing. It searches as every cell does (IntegerSearchModelTests).
// An Enum over Int64 that is never null is held by OneNumberSearchModelTests.
public sealed class EnumSearchModelTests
{
    public static IEnumerable<TheoryDataRow<string, bool>> Cells =>
    [
        new("Enum over Int16", true), new("Enum over Int16", false), new("Enum over Int64", true),
        new("Enum over UInt64", true), new("Enum over UInt64", false), new("Flags", true), new("Flags", false),
    ];

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_an_Enum_or_Flags_index_by_value_by_range_and_in_pages_agrees_with_the_model_in_both_directions_on_a_master_and_a_child(string type, bool nullable) =>
        ScalarTypes.Named(type).RunAsync(nullable);
}
