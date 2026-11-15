using System.Globalization;
using System.Text;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Values;

// The bound of one String or Binary value (the remarks of String and Binary of LatticeFieldTypeEnum;
// decision O19 of Epic 1): one value takes at most Bytes bytes of its stored encoding, for text the
// encoding the schema declares. A fixed rule of the storage format, the same on every node and not
// an option. The validation of the core checks it at a write and when an operation is added to a
// batch, before the conditions of the write and before a refusal as not implemented. The root writer
// of the engine keeps its own refusal of a larger value as the guard of the stored form: the
// capacity of one directory of chunks, ValueBlocks.MostValueBytes, which is the same number.
//
// Text is counted without a pass over it unless it is long enough to reach the bound. In UTF-16 a
// code unit takes two bytes. In UTF-8 it takes one to three, and a surrogate pair four for its two
// units, so text of at most Bytes / 3 code units is within the bound and text of more than Bytes
// code units above it, both known from the length; only text between the two is counted, in one
// pass.
internal static class ValueBound
{
    public const int Bytes = 268_173_376;

    // What a message of the validation says of a field whose value is above the bound.
    public static string Problem { get; } = string.Create(CultureInfo.InvariantCulture, $"takes more than the {Bytes:N0} bytes of its stored encoding that one value may take.");

    // Whether the value of a field is within the bound: a String or a Binary value of a field of
    // that type. Any other value is not judged here, and the rest of the validation checks it.
    public static bool Holds(LatticeField field, object value, LatticeStringEncodingEnum encoding) => (field.Declaration.Type, value) switch
    {
        (LatticeFieldTypeEnum.String, string text) => HoldsText(text, encoding),
        (LatticeFieldTypeEnum.Binary, byte[] bytes) => bytes.Length <= Bytes,
        _ => true,
    };

    private static bool HoldsText(string text, LatticeStringEncodingEnum encoding)
    {
        if (encoding == LatticeStringEncodingEnum.Utf16)
        {
            return text.Length <= Bytes / 2;
        }

        if (text.Length <= Bytes / 3)
        {
            return true;
        }

        if (text.Length > Bytes)
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(text) <= Bytes;
    }
}
