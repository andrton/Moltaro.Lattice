using Moltaro.Lattice.Tests.Indexes.Model;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A String part compares by the policy of its field, the same in a UTF-8 and in a UTF-16 database (decision
// on strings, Q04 and Q46; docs/design/milestone-1-engine-contract.md, "Index preparation and traversal":
// String comparison is Ordinal or OrdinalIgnoreCase, independent of the storage): Exact of Item is
// compared Ordinal, by its UTF-16 code units; Folded without case, by the code units of the simple
// uppercase mapping of the case table of the core (CaseRule). The same assertions run in both encodings:
// - equality: a text is found under its own spelling and, without case only, under another spelling;
// - order: the whole index in both directions is the order of the oracle;
// - a bound that differs from the stored values only by case is a bound of its own value under Ordinal
//   and of the values equal to it without case under OrdinalIgnoreCase, inclusive and exclusive, lower
//   and upper;
// - U+FF5E and U+10000, whose order differs between UTF-16 code units, where U+10000 is a surrogate pair
//   below U+FF5E, and UTF-8 bytes, where it is above, are in the order of their code units in a walk and
//   as a bound.
// TextKeySearchTests holds equality under both policies in both encodings, with every length of text and
// both storages; OrderAndBoundsTests the order of both policies in composite keys, with bounds that
// differ by case and U+FF5E beside U+1F600.
public sealed class TextPolicyTests
{
    private static readonly string[] Texts = ["", "a", "A", "ab", "AB", "b", "B", "é", "É", "～", "\U00010000", "z"];

    public static IEnumerable<TheoryDataRow<string, string>> Forms =>
        from encoding in new[] { "Utf8", "Utf16" }
        from text in new[] { "Exact", "Folded" }
        select new TheoryDataRow<string, string>(encoding, text);

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Text_part_is_equal_and_ordered_by_the_policy_of_its_field_in_either_encoding(string encoding, string field)
    {
        var order = OrderOf(field);
        var stored = BindingSearch.Numbered([.. Texts.Reverse(), null]);

        await using var database = await BindingSchema.OpenAsync(encoding);
        await BindingSearch.InsertAsync(database, field, stored);

        foreach (var text in new[] { "a", "A", "AB", "É", "～", "\U00010000" })
        {
            await BindingSearch.ExpectAsync(database, field, IndexKey.Equal(text), reverse: false, BindingSearch.Equal(stored, text, order), $"By{field} = \"{text}\"", order);
        }

        await BindingSearch.ExpectRangeAsync(database, field, stored, null, null, order);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Bound_that_differs_from_stored_values_only_by_case_bounds_by_the_policy_of_the_field(string encoding, string field)
    {
        var order = OrderOf(field);
        var stored = BindingSearch.Numbered([.. Texts, null]);

        await using var database = await BindingSchema.OpenAsync(encoding);
        await BindingSearch.InsertAsync(database, field, stored);

        // "Ab" and "aB" are spelt as no stored text is; "a" and "É" are stored beside their other case.
        foreach (var bound in new[] { "Ab", "aB", "a", "É" })
        {
            foreach (var inclusive in new[] { true, false })
            {
                await BindingSearch.ExpectRangeAsync(database, field, stored, new RangeEnd(bound, inclusive), null, order);
                await BindingSearch.ExpectRangeAsync(database, field, stored, null, new RangeEnd(bound, inclusive), order);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task U_FF5E_and_U_10000_are_in_the_order_of_their_UTF_16_code_units_in_a_walk_and_at_a_bound(string encoding, string field)
    {
        var order = OrderOf(field);
        var stored = BindingSearch.Numbered("～", "\U00010000", "\U00010000x", "～x", "�", "퟿", null);

        // In UTF-16 code units U+10000, 0xD800 0xDC00, comes before U+FF5E; in UTF-8 bytes, F0 90 80 80 after
        // EF BD 9E.
        Assert.True(string.CompareOrdinal("\U00010000", "～") < 0);

        await using var database = await BindingSchema.OpenAsync(encoding);
        await BindingSearch.InsertAsync(database, field, stored);

        await BindingSearch.ExpectRangeAsync(database, field, stored, null, null, order);

        foreach (var bound in new[] { "～", "\U00010000" })
        {
            foreach (var inclusive in new[] { true, false })
            {
                await BindingSearch.ExpectRangeAsync(database, field, stored, new RangeEnd(bound, inclusive), null, order);
                await BindingSearch.ExpectRangeAsync(database, field, stored, null, new RangeEnd(bound, inclusive), order);
            }
        }

        await BindingSearch.ExpectRangeAsync(database, field, stored, RangeEnd.Including("\U00010000"), RangeEnd.Excluding("～"), order);
    }

    // The order of the model for the field: the code units, or without case the code units of the simple
    // uppercase mapping.
    private static IComparer<object> OrderOf(string field) => field == "Exact" ? IndexModel.Ordinal : IndexModel.WithoutCase;
}
