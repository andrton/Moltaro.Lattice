namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// A search of an index of one temporal part agrees with the model of the order, the field nullable and not
// (docs/design/ordered-index.md, "Entries and their order": the temporal types by their ticks or day
// numbers; docs/decisions/schema-and-types.md: a DateTimeOffset is its UTC instant, one key at any offset,
// and a read returns it at the offset zero). A cell stores 400 entities over at least four leaves, the ends
// of the type among them, a DateTimeOffset at offsets of both signs, one instant at three offsets, and a
// negative TimeSpan (ScalarTypes); it searches as every cell does (IntegerSearchModelTests), with the values
// of a DateTimeOffset as they were given, at their offsets.
// A DateTimeOffset, a TimeOnly and a TimeSpan that are never null are held by OneNumberSearchModelTests.
public sealed class TemporalSearchModelTests
{
    public static IEnumerable<TheoryDataRow<string, bool>> Cells =>
    [
        new("DateTimeOffset", true), new("DateOnly", true), new("DateOnly", false), new("TimeOnly", true), new("TimeSpan", true),
    ];

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_a_temporal_index_by_value_by_range_and_in_pages_agrees_with_the_model_in_both_directions_on_a_master_and_a_child(string type, bool nullable) =>
        ScalarTypes.Named(type).RunAsync(nullable);
}
