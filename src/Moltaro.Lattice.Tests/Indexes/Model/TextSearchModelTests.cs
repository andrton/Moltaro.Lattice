namespace Moltaro.Lattice.Tests.Indexes.Model;

// A search of an index of one String part agrees with the model of the order, under each comparison and in
// each string encoding, the field nullable and not (docs/design/ordered-index.md, "Entries and their order"
// and "Text keys"; the decision on strings, Q46): Ordinal by UTF-16 code units, without case by the code
// units of the simple uppercase mapping, the same in a UTF-8 and in a UTF-16 database. A cell stores 400
// entities over at least five leaves: the empty text, case pairs inside and outside ASCII and the basic
// plane, characters on both sides of the surrogates, whose UTF-8 bytes sort the other way round from their
// code units, texts at 64 and 65 bytes in both encodings, where an entry stops holding a copy, at 256 and
// 257 code units, the piece of a decoded comparison, beyond 4 KiB, where a text lies in chunks, and beyond
// one chunk; and random short texts of those characters (ScalarTypes). It searches as every cell does
// (IntegerSearchModelTests).
public sealed class TextSearchModelTests
{
    public static IEnumerable<TheoryDataRow<string, string, bool>> Cells =>
        from comparison in new[] { "String Ordinal", "String without case" }
        from encoding in new[] { "Utf8", "Utf16" }
        from nullable in new[] { true, false }
        select new TheoryDataRow<string, string, bool>(comparison, encoding, nullable);

    [Theory]
    [MemberData(nameof(Cells))]
    public Task Search_of_a_text_index_by_value_by_range_and_in_pages_agrees_with_the_model_in_both_directions_on_a_master_and_a_child(string type, string encoding, bool nullable) =>
        ScalarTypes.Named(type).RunAsync(nullable, encoding);
}
