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
// - a String follows the comparison its field declares, with the same result in a UTF-8 and in a
//   UTF-16 database: Ordinal, the equality of the UTF-16 code units, or OrdinalIgnoreCase, by the case
//   table of Lattice's own (decision on strings, Q04, the addition of 15 November 2026). The rule of
//   equality and hash for text is the schema's, LatticeValueDeclaration.TextComparison, which the core
//   follows for Map keys, Set elements and the keys of a write batch as well; a key is compared with
//   stored text here, and nowhere else.
//
// How a stored text is compared with a given one. The stored text is read as UTF-16, a piece of
// PieceLength code units at a time, into a buffer on the stack (StoredTextReader), and each piece is
// compared with the same code units of the given text by the rule; the texts are equal when every
// piece is and both end together. That is the answer of the rule for the whole texts. Both
// comparisons hold texts of different lengths different and compare them place by place: a code unit
// with the code unit at the same place, and without case a surrogate pair with a pair at the same
// place, as one scalar value, whose mapping keeps the number of code units. No piece of the stored text
// ends inside a pair, so a pair of the stored text is always compared whole; where the given text has
// the first half of a pair at the end of its piece, the stored text has another character at that
// place, which a high surrogate standing for itself equals under neither comparison, so both the
// pieces and the whole texts differ. No byte order of UTF-8 stands for UTF-16, no culture is consulted,
// and nothing of the casing of the runtime is called.
//
// The hash of a key in the primary index (TextHash) is the engine's own, seeded with the seed of the
// instance, so that the index depends on nothing a process sets and nobody who does not know the seed
// can choose keys that share a hash (the planner's answer to Q50): Marvin32, the hash of text of the
// .NET runtime (Marvin), over the UTF-16 code units of the text, and for a comparison without case
// over the code units the case rule maps the text to (TextComparison.ToUpper), the mapping every hash
// without case follows. It is a hash of code units, so a text hashes the same in a UTF-8 and in a
// UTF-16 database; the engine computes it from the text a caller gives, when it makes the key
// (Sets.KeyArgument), before the read enters the protection of reads, and the index keeps it in its
// entry, so no stored text is ever hashed.
//
// What it reserves: nothing. The buffer is 512 bytes of the stack of the comparison, and a comparison
// allocates nothing, whatever the length of the text and wherever it lies. The hash without case maps
// the text into 512 bytes of the stack as well.
//
// What a lookup costs: the hash of the given text, one pass over it; and for each key of the index
// with the same hash, in practice the one that is looked for, one pass over its stored text, decoded
// from UTF-8 or copied, and one pass of the rule over both. Without case a code unit that equals its
// counterpart needs no mapping, and one that does not is looked up in the case table.
internal static class ValueComparer
{
    // The code units of stored text compared at a time.
    private const int PieceLength = 256;

    // The hash of a text in the primary index under the comparison the String field declares and the
    // seed (see the class): texts that are equal under the comparison have the same hash under every
    // seed. Allocates nothing.
    public static uint TextHash(LatticeField field, ReadOnlySpan<char> text, ulong seed) =>
        ComparisonOf(field).IgnoresCase ? TextHashWithoutCase(text, seed) : Marvin.Hash(text, seed);

    // The hash without case: the text mapped a piece of PieceLength code units at a time, and the
    // pieces hashed one after the other as one text. A method of its own, so that the hash with
    // Ordinal does not set up the buffer on the stack: measured in isolation on keys of 13 characters,
    // best of 21 rounds, with the buffer in TextHash the hash with Ordinal took 10.8 to 12.7 ns, and
    // 9.3 to 9.9 ns here, as the hash of .NET did before the seed.
    private static uint TextHashWithoutCase(ReadOnlySpan<char> text, ulong seed)
    {
        Span<char> mapped = stackalloc char[PieceLength];
        var marvin = new Marvin(seed);

        while (!text.IsEmpty)
        {
            var length = TextComparison.ToUpper(text, mapped);

            marvin.Append(mapped[..length]);
            text = text[length..];
        }

        return marvin.Finish();
    }

    // Whether the stored text of the String field of the root is the given text under the comparison
    // the field declares. Allocates nothing.
    public static bool TextEquals(in RootReader root, LatticeField field, ReadOnlySpan<char> text) =>
        EqualsUnder(root, field, text, ComparisonOf(field));

    // Whether the stored text of the String field of the root is the given text code unit for code
    // unit, whatever the field declares: whether it has the same spelling. Allocates nothing.
    public static bool SameText(in RootReader root, LatticeField field, ReadOnlySpan<char> text) =>
        EqualsUnder(root, field, text, TextComparison.Ordinal);

    private static bool EqualsUnder(in RootReader root, LatticeField field, ReadOnlySpan<char> text, TextComparison comparison)
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

            if (length > text.Length || !comparison.Equals(piece[..length], text[..length]))
            {
                return false;
            }

            text = text[length..];
        }
    }

    // The comparison of a String field, as the schema gives it to the core and to the engine alike.
    private static TextComparison ComparisonOf(LatticeField field) => field.Declaration.TextComparison!;
}
