using System.Runtime.CompilerServices;
using System.Text;

namespace Moltaro.Lattice.Schema;

// How text at a position of the schema is compared: the one rule of equality and hash for text, which
// a declaration gives as LatticeValueDeclaration.TextComparison and which every comparison of text
// reads, in the core (the Dictionary and HashSet a record holds for a Map and a Set, the identity of an
// entity in a write batch) and in the engine (Engine.Comparison.ValueComparer, which compares a key with
// stored text a piece at a time). The engine references the core and the core cannot reference the
// engine, so the rule lives here.
//
// - Ordinal: two texts are equal when their UTF-16 code units are equal, one by one. The hash is the
//   ordinal hash of .NET over the code units, which depends on no culture and no globalization mode.
// - Without case (OrdinalIgnoreCase): two texts are equal when they are equal scalar value by scalar
//   value after each scalar value is replaced by its simple uppercase mapping of the case table of
//   Lattice's own (CaseTable; decision on strings, Q04, the addition of 15 November 2026). A surrogate
//   pair is one scalar value; a surrogate without its pair stands for itself. The hash is the ordinal
//   hash of the mapped text, which every text equal to it maps to, code unit for code unit: a mapping
//   never changes the number of code units of a scalar value (CaseTable). No call of the runtime that
//   depends on the globalization mode is made, for the equality or for the hash.
//
// Both work on spans, without allocating: the hash maps the text into a buffer of PieceLength code
// units on the stack, a piece at a time. The class is sealed and its members are called directly, so a
// comparison costs no virtual call and no object per character; the lookup of a scalar value is
// inlined into the walk over the text. Text of ASCII takes a fast path of the ASCII methods of .NET,
// which are vectorized and depend on no globalization. Measured by a throwaway benchmark, best of
// seven rounds, for one hash and one equality under another spelling: keys of 13 ASCII characters,
// as those of the scenario read-by-text-key, 40 ns, and 73 ns without the fast path; keys of 15
// characters that begin in Cyrillic, 94 ns, and 151 ns without the inlining.
// StringComparison.OrdinalIgnoreCase of .NET took 29 and 73 ns.
internal sealed class TextComparison : IEqualityComparer<string>
{
    // The code units of text mapped at a time for a hash.
    private const int PieceLength = 256;

    private TextComparison(bool ignoresCase)
    {
        IgnoresCase = ignoresCase;
    }

    public static TextComparison Ordinal { get; } = new(ignoresCase: false);

    public static TextComparison WithoutCase { get; } = new(ignoresCase: true);

    // Whether this is the comparison without case.
    public bool IgnoresCase { get; }

    public bool Equals(ReadOnlySpan<char> left, ReadOnlySpan<char> right) =>
        IgnoresCase ? EqualWithoutCase(left, right) : left.SequenceEqual(right);

    // Texts that are equal have the same hash.
    public int GetHashCode(ReadOnlySpan<char> text) =>
        IgnoresCase ? HashWithoutCase(text) : string.GetHashCode(text);

    public bool Equals(string? x, string? y) =>
        ReferenceEquals(x, y) || (x is not null && y is not null && Equals(x.AsSpan(), y.AsSpan()));

    public int GetHashCode(string obj) => GetHashCode(obj.AsSpan());

    // Scalar value by scalar value; a code unit that equals the one at its place in the other text and
    // begins no surrogate pair needs no mapping. Two texts of ASCII are compared at once: within ASCII
    // the table maps a to z to A to Z and nothing else, which is what Ascii.EqualsIgnoreCase holds
    // equal, and the ASCII methods depend on no globalization.
    private static bool EqualWithoutCase(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        if (Ascii.IsValid(left) && Ascii.IsValid(right))
        {
            return Ascii.EqualsIgnoreCase(left, right);
        }

        var index = 0;

        while (index < left.Length)
        {
            if (left[index] == right[index] && !char.IsHighSurrogate(left[index]))
            {
                index++;

                continue;
            }

            var length = ScalarValueAt(left, index, out var leftValue);

            // A pair in one text against a single code unit in the other: their mappings differ, since
            // a mapping keeps the number of code units.
            if (ScalarValueAt(right, index, out var rightValue) != length || CaseTable.ToUpper(leftValue) != CaseTable.ToUpper(rightValue))
            {
                return false;
            }

            index += length;
        }

        return true;
    }

    private static int HashWithoutCase(ReadOnlySpan<char> text)
    {
        Span<char> mapped = stackalloc char[PieceLength];
        var hash = 0;

        while (!text.IsEmpty)
        {
            var length = ToUpper(text, mapped);

            hash = HashCode.Combine(hash, string.GetHashCode(mapped[..length]));
            text = text[length..];
        }

        return hash;
    }

    // Maps the text into the destination, scalar value by scalar value, as far as it fits without
    // cutting a surrogate pair, and returns how many code units it mapped: as many as it wrote. Equal
    // texts are cut at the same places, since they have their pairs at the same places. The ASCII that
    // begins the text is mapped at once by Ascii.ToUpper, which maps a to z as the table does and stops
    // at the first code unit that is not ASCII.
    private static int ToUpper(ReadOnlySpan<char> text, Span<char> destination)
    {
        var length = Math.Min(text.Length, destination.Length);

        Ascii.ToUpper(text[..length], destination, out var index);

        while (index < length)
        {
            if (ScalarValueAt(text, index, out var value) == 1)
            {
                destination[index++] = (char)CaseTable.ToUpper(value);

                continue;
            }

            if (index + 1 == length)
            {
                break;
            }

            var upper = CaseTable.ToUpper(value);

            destination[index++] = (char)(0xD7C0 + (upper >> 10));
            destination[index++] = (char)(0xDC00 + (upper & 0x3FF));
        }

        return index;
    }

    // The scalar value at the place and the code units it takes: two for a surrogate pair, one for any
    // other code unit, a surrogate without its pair included, which stands for itself.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScalarValueAt(ReadOnlySpan<char> text, int index, out int value)
    {
        var unit = text[index];

        if (char.IsHighSurrogate(unit) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            value = char.ConvertToUtf32(unit, text[index + 1]);

            return 2;
        }

        value = unit;

        return 1;
    }
}
