namespace Moltaro.Lattice.Engine.Tests;

// The bound of one String or Binary value (decision O19; RootWriter; the remarks of String and Binary
// of LatticeFieldTypeEnum): one value takes at most 268,173,376 bytes of its stored encoding, the
// 4,094 chunks of 65,504 bytes that one directory block of chunks holds. A fixed rule of the storage
// format, not an option.
//
// And one text at the bound, which the tests of the root writer and of the engine database share,
// because it takes 179 MB: 89,391,125 times € and two a. In UTF-8, where € takes three bytes, it is
// 268,173,377 bytes, one more than the bound, and without its last character exactly the bound; in
// UTF-16 it is 178,782,254 bytes, below the bound.
internal static class ValueBound
{
    public const int Bytes = 268_173_376;

    private const int Euros = 89_391_125;

    private static readonly Lazy<string> OneByteAbove = new(() => string.Create(Euros + 2, 0, static (characters, _) =>
    {
        characters[..Euros].Fill('€');
        characters[Euros..].Fill('a');
    }));

    // The text of 268,173,377 bytes in UTF-8.
    public static string TextOneByteAboveInUtf8 => OneByteAbove.Value;
}
