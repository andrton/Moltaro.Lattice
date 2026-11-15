using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Comparison;

// The equality and the hash of values by the rules the schema declares: the one comparison and hash
// service of the schema that the milestone contract asks for ("Index preparation and traversal"),
// across indexes, Map and Set and direct lookups. From S07 the primary index uses it, and nothing else
// in the engine compares a key with stored text. It knows the comparison from the declaration of the
// field and the encoding of the database from the root layout, so it has no state of its own and one
// set of rules serves every database. S14 adds the order of values, S19 the members of a Map and a Set.
//
// What equality is, for each type of a primary key:
// - an integer and a Guid are held in the index as their value, and the equality and the hash of that
//   .NET value are the key's: two integers of the key type are one key when they are the same number,
//   two Guids when their 16 bytes are the same (Entities.EntityTable says how each is held);
// - a String follows the comparison its field declares, StringComparison.Ordinal or
//   StringComparison.OrdinalIgnoreCase of .NET exactly (Q04: the .NET ordinal comparisons are the
//   semantic reference), with the same result in a UTF-8 and in a UTF-16 database. Here, and nowhere
//   else.
//
// How a stored text is compared with a given one. The stored text is read as UTF-16, a piece of
// PieceLength code units at a time, into a buffer on the stack (StoredTextReader), and each piece is
// compared with the same code units of the given text by MemoryExtensions.Equals under the comparison;
// the texts are equal when every piece is and both end together. That is the answer .NET gives for the
// whole texts. Both comparisons hold texts of different lengths different and compare them place by
// place: a code unit with the code unit at the same place, and under OrdinalIgnoreCase a surrogate
// pair with a pair at the same place, as one code point. No piece of the stored text ends inside a
// pair, so a pair of the stored text is always compared whole; where the given text has the first
// half of a pair at the end of its piece, the stored text has another character at that place, which
// a high surrogate equals under neither comparison, so both the pieces and the whole texts differ.
// No case folding of the project's own is made, no byte order of UTF-8 stands for UTF-16, and no
// culture is consulted.
//
// What it reserves: nothing. The buffer is 512 bytes of the stack of the comparison, and a comparison
// allocates nothing, whatever the length of the text and wherever it lies. The hash is computed by
// .NET from the given text: under OrdinalIgnoreCase .NET takes the upper case of a text that is not
// all ASCII into a workspace of its own, on the stack for a short text and rented from
// ArrayPool<char>.Shared for a long one, and gives it back before it returns, so after a first hash
// of a text of that length on a thread it allocates nothing. The engine computes the hash of a key
// before it takes a gate (Sets.KeyArgument), so that workspace is never taken under one.
//
// What a lookup costs: the hash of the given text, one pass over it; and for each key of the index
// with the same hash, in practice the one that is looked for, one pass over its stored text, decoded
// from UTF-8 or copied, and one pass of .NET over both. The same for both comparisons; under
// OrdinalIgnoreCase .NET maps the case of every code unit that is not ASCII.
internal static class ValueComparer
{
    // The code units of stored text compared at a time.
    private const int PieceLength = 256;

    // The hash of a text under the comparison the String field declares: texts that are equal under
    // it have the same hash. Computed from the given text, never from stored text.
    public static int TextHash(LatticeField field, ReadOnlySpan<char> text) => string.GetHashCode(text, ComparisonOf(field));

    // Whether the stored text of the String field of the root is the given text under the comparison
    // the field declares. Allocates nothing.
    public static bool TextEquals(in RootReader root, LatticeField field, ReadOnlySpan<char> text) =>
        EqualsUnder(root, field, text, ComparisonOf(field));

    // Whether the stored text of the String field of the root is the given text code unit for code
    // unit, whatever the field declares: whether it has the same spelling. Allocates nothing.
    public static bool SameText(in RootReader root, LatticeField field, ReadOnlySpan<char> text) =>
        EqualsUnder(root, field, text, StringComparison.Ordinal);

    private static bool EqualsUnder(in RootReader root, LatticeField field, ReadOnlySpan<char> text, StringComparison comparison)
    {
        var stored = root.ReadText(field);
        Span<char> piece = stackalloc char[PieceLength];

        while (true)
        {
            var length = stored.Read(piece);

            if (length == 0)
            {
                return text.IsEmpty;
            }

            if (length > text.Length || !MemoryExtensions.Equals(piece[..length], text[..length], comparison))
            {
                return false;
            }

            text = text[length..];
        }
    }

    private static StringComparison ComparisonOf(LatticeField field) =>
        field.Declaration.Comparison == LatticeStringComparisonEnum.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
