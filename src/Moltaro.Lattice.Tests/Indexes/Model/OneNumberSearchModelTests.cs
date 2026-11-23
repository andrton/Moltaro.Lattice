namespace Moltaro.Lattice.Tests.Indexes.Model;

// The index of one part, never null, whose stored form is a signed integer of 8 bytes — an Int64, an Enum
// over Int64, a DateTimeOffset as its UTC ticks, a TimeOnly and a TimeSpan as their ticks — is ordered by
// that number without the loop over the parts (docs/design/ordered-index.md, "Entries and their order").
// Its search agrees with the model of the order as every other one does: 400 entities over at least seven
// leaves, values of both signs where the type has them, Int64, the Enum and TimeSpan, with the ends of the
// type, zero and the values on both sides of zero among them, walked in order and in reverse, by value, by
// range and in pages, through the class and by name, on a master and on a child (ScalarTypes, ModelRun).
// The nullable cells of these types take the general order and are held by IntegerSearchModelTests,
// EnumSearchModelTests and TemporalSearchModelTests.
public sealed class OneNumberSearchModelTests
{
    public static IEnumerable<TheoryDataRow<string>> Types =>
        [new("Int64"), new("Enum over Int64"), new("DateTimeOffset"), new("TimeOnly"), new("TimeSpan")];

    [Theory]
    [MemberData(nameof(Types))]
    public Task Search_of_an_index_of_one_signed_eight_byte_number_never_null_walks_negative_and_positive_values_in_the_order_of_the_model_by_value_and_by_range(string type) =>
        ScalarTypes.Named(type).RunAsync(nullable: false);
}
