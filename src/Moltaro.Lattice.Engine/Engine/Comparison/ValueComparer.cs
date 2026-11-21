using System.Runtime.CompilerServices;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Comparison;

// The equality, the hash and the order of values by the rules the schema declares: the one comparison
// and hash service of the schema that the milestone contract asks for ("Index preparation and
// traversal"), across indexes, Map and Set and direct lookups. The primary index uses its equality and
// its hash (TextHash, the seeded Marvin32 of the code units), the secondary indexes its order
// (CompareStored, CompareText, CompareUtf8Ordinal; Indexes.IndexOrder), and nothing else in the engine
// compares a key with stored text. A text compares the same in a UTF-8 and in a UTF-16 database, by the
// comparison its field declares, Ordinal or OrdinalIgnoreCase by the case table of Lattice's own; a
// stored text is read a piece of PieceLength code units at a time (StoredTextReader), so a comparison
// allocates nothing, whatever the length of the text and wherever it lies; its buffers on the stack are
// not zeroed (SkipLocalsInit), since every code unit a comparison reads it wrote first. It has no state
// of its own: the comparison comes from the declaration of the field and the encoding from what reads
// the text, so one set of rules serves every database. The members of a Map and a Set are not stored yet.
//
// docs/design/arena-protocol.md, "The comparison of a key", says what equality is for each type of a
// primary key, how a text is compared by pieces and its surrogate pairs whole, how the hash is seeded,
// and what a lookup costs; docs/design/ordered-index.md, "Entries and their order", gives the order of
// each type with its decision, and the turn that orders the UTF-8 bytes of a text as its code units.
[SkipLocalsInit]
internal static class ValueComparer
{
    // The code units of stored text compared at a time.
    private const int PieceLength = 256;

    // The hash of a text in the primary index under the comparison the String field declares and the
    // seed (arena-protocol.md, "The comparison of a key"): texts that are equal under the comparison
    // have the same hash under every seed. Allocates nothing.
    public static uint TextHash(LatticeField field, ReadOnlySpan<char> text, ulong seed) =>
        ComparisonOf(field).IgnoresCase ? TextHashWithoutCase(text, seed) : Marvin.Hash(text, seed);

    // The same hash of a stored text, read a piece at a time, so that the key a root stores is hashed as
    // the text of that key a caller gives is: for the keys a search through a secondary index remembers
    // (Entities.PrimaryKeys.StoredHash). Allocates nothing. The walk of ranges is not implemented yet.
    public static uint TextHash(LatticeField field, StoredTextReader text, ulong seed) =>
        throw new NotSupportedException("The walk of ranges is not implemented yet.");

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

    // The order of two stored values of the fixed type given, from their places at the width of the type
    // (ordered-index.md, "Entries and their order"): below zero when the left one comes first. Allocates
    // nothing.
    public static int CompareStored(StoredTypeEnum type, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) => type switch
    {
        StoredTypeEnum.Boolean => left[0].CompareTo(right[0]),
        StoredTypeEnum.SignedInteger => StoredScalars.ReadInteger(left).CompareTo(StoredScalars.ReadInteger(right)),
        StoredTypeEnum.UnsignedInteger => StoredScalars.ReadUnsigned(left).CompareTo(StoredScalars.ReadUnsigned(right)),
        StoredTypeEnum.Float32 => StoredScalars.ReadFloat32(left).CompareTo(StoredScalars.ReadFloat32(right)),
        StoredTypeEnum.Float64 => StoredScalars.ReadFloat64(left).CompareTo(StoredScalars.ReadFloat64(right)),
        StoredTypeEnum.Decimal => decimal.Compare(StoredScalars.ReadDecimal(left), StoredScalars.ReadDecimal(right)),
        StoredTypeEnum.Guid => StoredScalars.ReadGuid(left).CompareTo(StoredScalars.ReadGuid(right)),
        StoredTypeEnum.DateTimeOffset => StoredScalars.ReadDateTimeOffset(left).CompareTo(StoredScalars.ReadDateTimeOffset(right)),
        StoredTypeEnum.DateOnly => StoredScalars.ReadDateOnly(left).CompareTo(StoredScalars.ReadDateOnly(right)),
        StoredTypeEnum.TimeOnly => StoredScalars.ReadTimeOnly(left).CompareTo(StoredScalars.ReadTimeOnly(right)),
        StoredTypeEnum.TimeSpan => StoredScalars.ReadTimeSpan(left).CompareTo(StoredScalars.ReadTimeSpan(right)),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a fixed-width type."),
    };

    // The order of two stored texts under the comparison given (arena-protocol.md, "The comparison of a
    // key"): below zero when the left one comes first. Allocates nothing.
    public static int CompareText(StoredTextReader left, StoredTextReader right, TextComparison comparison)
    {
        Span<char> leftPiece = stackalloc char[PieceLength];
        Span<char> rightPiece = stackalloc char[PieceLength];

        if (!comparison.IgnoresCase)
        {
            var leftUnits = new Units(left, leftPiece, default, ignoresCase: false);
            var rightUnits = new Units(right, rightPiece, default, ignoresCase: false);

            return Compare(ref leftUnits, ref rightUnits);
        }

        Span<char> leftMapped = stackalloc char[PieceLength];
        Span<char> rightMapped = stackalloc char[PieceLength];
        var leftMappedUnits = new Units(left, leftPiece, leftMapped, ignoresCase: true);
        var rightMappedUnits = new Units(right, rightPiece, rightMapped, ignoresCase: true);

        return Compare(ref leftMappedUnits, ref rightMappedUnits);
    }

    // The order of a stored text and a given one under the comparison given: below zero when the stored
    // one comes first. Allocates nothing.
    public static int CompareText(StoredTextReader stored, ReadOnlySpan<char> given, TextComparison comparison)
    {
        Span<char> storedPiece = stackalloc char[PieceLength];

        if (!comparison.IgnoresCase)
        {
            // Both scoped to this block, the stored ones holding a piece of this frame, so that they may be
            // compared with one another.
            scoped var storedUnits = new Units(stored, storedPiece, default, ignoresCase: false);
            scoped var givenUnits = new Units(given, default, ignoresCase: false);

            return Compare(ref storedUnits, ref givenUnits);
        }

        Span<char> storedMapped = stackalloc char[PieceLength];
        Span<char> givenMapped = stackalloc char[PieceLength];
        var storedMappedUnits = new Units(stored, storedPiece, storedMapped, ignoresCase: true);
        var givenMappedUnits = new Units(given, givenMapped, ignoresCase: true);

        return Compare(ref storedMappedUnits, ref givenMappedUnits);
    }

    // The order of two texts of a UTF-8 database under Ordinal, from their bytes: below zero when the left
    // one comes first, the order of their UTF-16 code units. Allocates nothing. UTF-8 bytes sort as code
    // points (RFC 3629), which is the order of the code units except at one place: a character of U+E000 to
    // U+FFFF, lead byte 0xEE or 0xEF, comes before one beyond U+FFFF, lead byte 0xF0 or above, by code
    // point, and after it by code unit, since UTF-16 writes the latter with surrogates, which lie below
    // 0xE000. The texts are well-formed, so the first byte that differs is the lead byte of a character on
    // both sides, or a continuation byte on both, inside characters with the same lead byte. The order
    // turns around when one of those bytes is 0xEE or 0xEF and the other 0xF0 or above: two lead bytes of
    // the two ranges. A continuation byte (0x80 to 0xBF) never meets that test, and is right not to, since
    // two characters with one lead byte lie in the same range. Text of a UTF-16 database has no such path:
    // its little-endian bytes are not in the order of its code units.
    public static int CompareUtf8Ordinal(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var common = left.CommonPrefixLength(right);

        if (common == left.Length || common == right.Length)
        {
            return left.Length.CompareTo(right.Length);
        }

        var leftByte = left[common];
        var rightByte = right[common];

        // A character beyond U+FFFF against one of U+E000 to U+FFFF: the order turns around.
        if (leftByte >= 0xF0 && rightByte is 0xEE or 0xEF)
        {
            return -1;
        }

        if (rightByte >= 0xF0 && leftByte is 0xEE or 0xEF)
        {
            return 1;
        }

        return leftByte < rightByte ? -1 : 1;
    }

    // The order of the code units of two texts, read a piece at a time: the first place where they
    // differ decides, and a text that ends first comes first.
    private static int Compare(scoped ref Units left, scoped ref Units right)
    {
        while (true)
        {
            var leftEnds = left.Current.IsEmpty && !left.Next();
            var rightEnds = right.Current.IsEmpty && !right.Next();

            if (leftEnds || rightEnds)
            {
                return leftEnds == rightEnds ? 0 : leftEnds ? -1 : 1;
            }

            var length = Math.Min(left.Current.Length, right.Current.Length);
            var order = left.Current[..length].SequenceCompareTo(right.Current[..length]);

            if (order != 0)
            {
                return order;
            }

            left.Current = left.Current[length..];
            right.Current = right.Current[length..];
        }
    }

    // The comparison of a String field, as the schema gives it to the core and to the engine alike.
    private static TextComparison ComparisonOf(LatticeField field) => field.Declaration.TextComparison!;

    // The code units of one text for the order, a piece at a time: of a stored text, read into a piece of
    // the stack, which ends between two characters; of a given text, as it is. Without case each piece is
    // mapped through the case table of the core, which never cuts a surrogate pair, into a buffer of the
    // stack. Current is what is left of the piece being compared.
    private ref struct Units
    {
        private readonly bool _isStored;

        private readonly bool _ignoresCase;

        private readonly Span<char> _piece;

        private readonly Span<char> _mapped;

        private StoredTextReader _stored;

        private ReadOnlySpan<char> _given;

        public Units(StoredTextReader stored, Span<char> piece, Span<char> mapped, bool ignoresCase)
        {
            _isStored = true;
            _stored = stored;
            _piece = piece;
            _mapped = mapped;
            _ignoresCase = ignoresCase;
        }

        public Units(ReadOnlySpan<char> given, Span<char> mapped, bool ignoresCase)
        {
            _given = given;
            _mapped = mapped;
            _ignoresCase = ignoresCase;
        }

        public ReadOnlySpan<char> Current { get; set; }

        // Reads the next piece into Current: false at the end of the text.
        public bool Next()
        {
            ReadOnlySpan<char> units;

            if (_isStored)
            {
                units = _piece[.._stored.Read(_piece)];
            }
            else
            {
                units = _given;
            }

            if (units.IsEmpty)
            {
                return false;
            }

            if (!_ignoresCase)
            {
                Current = units;
                _given = default;

                return true;
            }

            // A piece of stored text holds whole characters, so all of it is mapped; of a given text as
            // much as the buffer holds without cutting a pair, and the rest comes with the next piece.
            var mapped = TextComparison.ToUpper(units, _mapped);

            Current = _mapped[..mapped];

            if (!_isStored)
            {
                _given = _given[mapped..];
            }

            return true;
        }
    }
}
