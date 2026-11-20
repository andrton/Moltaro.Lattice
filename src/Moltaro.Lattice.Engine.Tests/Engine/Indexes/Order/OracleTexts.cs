using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Order;

// The texts of the oracle of the order of text, and the oracle: the order Ordinal of a String part is that
// of its UTF-16 code units, the same in a UTF-8 and in a UTF-16 database (decision on queries, "String
// ordering and query overrides"; docs/design/ordered-index.md, "Entries and their order"), and
// ValueComparer.CompareText, the comparison an index uses today, reading the stored text a piece at a time,
// is the oracle a faster comparison is held against. UTF-8 bytes sort as code points, which is the order of
// UTF-16 code units except where one side has a character from U+E000 to U+FFFF and the other a
// supplementary character, so the texts are chosen around there:
// - Exhaustive: every code point from U+D000 to U+D7FF, below the surrogates, and from U+E000 to U+FFFF,
//   above them; the supplementary characters of the first and of the last high surrogate, U+10000 to
//   U+103FF and U+10FC00 to U+10FFFF, which take every low surrogate; and one of every other high
//   surrogate. Each between a head and a tail, "p" and "z", so that two of them, or one of them and a
//   representative, differ first at that character, and the tail would order them the other way.
// - Representatives: characters on both sides of every place where the length of a UTF-8 sequence or the
//   order of the encodings changes, each between "p" and the tail "a"; and "p" alone and "p" with the
//   character and no tail, which end first.
// A surrogate without its pair is no text: the validation of a write refuses it, and a key given to a
// search with one is an ArgumentException, so none is among them.
internal static class OracleTexts
{
    private static readonly int[] RepresentativeCodePoints =
    [
        0x61, 0x7F, 0x80, 0x7FF, 0x800, 0xD7FF, 0xE000, 0xEFFF, 0xF000, 0xFFFD, 0xFFFF,
        0x10000, 0x103FF, 0x1F600, 0xFFFFF, 0x100000, 0x10FFFF,
    ];

    public static IReadOnlyList<string> Exhaustive { get; } = ExhaustiveTexts();

    public static IReadOnlyList<string> Representatives { get; } = RepresentativeTexts();

    // The layout of the index ByText of Keyed (OrderSchema) in a database of the encoding given.
    public static IndexLayout LayoutOf(string encoding)
    {
        var schema = OrderSchema.Of(encoding);

        Assert.True(schema.TryGetEntityType("Keyed", out var keyed));
        Assert.True(keyed.TryGetIndex("ByText", out var byText));

        return IndexLayout.Compile(byText, RootLayout.Compile(keyed, schema));
    }

    // The bytes the layout stores a text in.
    public static byte[] StoredOf(string text, IndexLayout layout) =>
        TextTally.EncodingOf(layout.Encoding == LatticeStringEncodingEnum.Utf8 ? "Utf8" : "Utf16").GetBytes(text);

    // The sign of the order of two texts by the oracle, under the comparison of the one part of the layout:
    // the left one as its stored bytes, the right one as it is given.
    public static int Order(ReadOnlySpan<byte> storedLeft, string right, IndexLayout layout) =>
        Math.Sign(ValueComparer.CompareText(new StoredTextReader(storedLeft, layout.Encoding), right, layout.Parts[0].Comparison!));

    private static List<string> ExhaustiveTexts()
    {
        var codePoints = Enumerable.Range(0xD000, 0x800)
            .Concat(Enumerable.Range(0xE000, 0x2000))
            .Concat(Enumerable.Range(0x10000, 0x400))
            .Concat(Enumerable.Range(0x10FC00, 0x400))
            .Concat(Enumerable.Range(1, 0x3FE).Select(high => 0x10000 + (high * 0x400) + (high % 0x400)));

        return [.. codePoints.Select(codePoint => $"p{char.ConvertFromUtf32(codePoint)}z")];
    }

    private static List<string> RepresentativeTexts()
    {
        var texts = new List<string> { "p" };

        foreach (var codePoint in RepresentativeCodePoints)
        {
            var character = char.ConvertFromUtf32(codePoint);

            texts.Add($"p{character}");
            texts.Add($"p{character}a");
        }

        return texts;
    }
}
