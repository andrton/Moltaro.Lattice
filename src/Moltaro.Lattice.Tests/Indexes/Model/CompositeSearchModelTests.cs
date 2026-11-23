namespace Moltaro.Lattice.Tests.Indexes.Model;

// A search of a composite index of parts of mixed widths agrees with the model of the order, in a UTF-8 and
// in a UTF-16 database (docs/design/ordered-index.md, "Entries and their order" and "How a range is
// walked"): by every count of leading parts, from none to all, each a key the entities have or one none
// has, and by a range on the part after equal leading parts, with bounds on stored values, between them and
// beyond both ends; in both directions and in pages, through the class and by name, on a master and on a
// child. The keys are (Guid, Decimal?, String without case), (DateOnly, Float64?, Int16) and the four
// parts (Boolean?, UInt32, DateTimeOffset?, String?) (CompositeKeys); each cell stores 400 entities.
public sealed class CompositeSearchModelTests
{
    public static IEnumerable<TheoryDataRow<string, string>> Cells =>
        from key in new[] { "Payment", "Reading", "Visit" }
        from encoding in new[] { "Utf8", "Utf16" }
        select new TheoryDataRow<string, string>(key, encoding);

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_a_composite_index_by_every_count_of_leading_parts_and_a_range_on_the_next_agrees_with_the_model_on_a_master_and_a_child(string key, string encoding) =>
        CompositeKeys.RunAsync(key, encoding);
}
