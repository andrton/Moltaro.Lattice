using System.Text;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Text;

// Text for the tests of String values: well-formed text of every encoded width, and text that is
// not well-formed UTF-16 because a surrogate lacks its pair.
internal static class TextSamples
{
    // Well-formed text: ASCII; characters of two, three and four bytes in UTF-8, the last as
    // surrogate pairs in UTF-16; combining sequences, which are kept as they are and not
    // normalized; and characters that only look special: NUL, a byte order mark, the replacement
    // character, a noncharacter.
    public static readonly IReadOnlyList<string> WellFormed =
    [
        "",
        "Plain ASCII, digits 0123456789 and punctuation: !?,.;",
        "Grüße aus Ærøskøbing, Привет, Ελλάδα",
        "漢字かなカナ €₴",
        "😀🎉𝄞𐍈",
        "é ạ̈ ñ é",
        "a\0b",
        "﻿bom � replacement ￿",
        "שלום مرحبا",
        "mixed: a é 漢 😀 é",
    ];

    // Text with a surrogate that lacks its pair: a lone high surrogate, a lone low one and a pair in
    // the wrong order, each alone, at the start, in the middle and at the end.
    public static readonly IReadOnlyList<string> Malformed =
    [
        "\uD83D",
        "\uDE00",
        "\uDE00\uD83D",
        "\uD83Dabc",
        "ab\uD83Dcd",
        "abc\uD83D",
        "\uDE00abc",
        "ab\uDE00cd",
        "abc\uDE00",
        "\uDE00\uD83Dabc",
        "ab\uDE00\uD83Dcd",
        "abc\uDE00\uD83D",
        "\uD83D😀",
        "😀\uD83D😀",
    ];

    // The bytes of the text in the encoding of the database, as GetLength counts them: the UTF-8
    // of its characters, or two bytes for each UTF-16 code unit.
    public static int EncodedLength(string text, string encoding) =>
        encoding == "Utf8" ? Encoding.UTF8.GetByteCount(text) : 2 * text.Length;
}
