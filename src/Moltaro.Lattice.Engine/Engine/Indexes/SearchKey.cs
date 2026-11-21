using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Unicode;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Indexes;

// What a search compares the entries of an index with: the values of a key bound to the fields of the
// index (Access.Sets.IndexCriteria), one for each of its leading parts, and where it stands among the
// entries those parts equal: at an identity, so that the first entry at or after it is the first entry of
// the key whose identity is From or later (docs/design/ordered-index.md, "The search by a full key"), or
// before or after every one of them, an edge, for the start and the end of a walk ("How a range is
// walked"). An edge is an identity no root entry has, below or above every one (default, AfterEvery), so
// that the order compares the parts the key gives and then From, and needs no other rule for it.
//
// It is made once for each step of a search, outside the protection of reads, and allocates nothing: the
// values of the fixed-width parts are written into scratch the caller gives on its stack, after the null
// bitmap, by the one writer of the stored form of a value, the one the root writer uses
// (Storage.Values.StoredScalars), so that such a part compares with the part of an entry as two
// stored values; a String part stays the text of the key, which the comparison reads against the
// stored text a piece at a time. A String part compared Ordinal in a UTF-8 database is written into the
// scratch as its UTF-8 bytes as well, when they fit TextBytesPerPart, so that the order compares it with
// the bytes of the stored text (Comparison.ValueComparer.CompareUtf8Ordinal); a longer text, or one that
// is not well-formed, has no bytes and is compared decoded. A key has at most four values
// (IndexKey.Equal) and one bound of a range after them, so ScratchBytes always holds them.
//
// A readonly ref struct: it refers to the scratch and to the bound key, which it does not copy and which
// its caller holds for the whole search (Sets.IndexMatches, Sets.SetIndex), and lives on the stack of the
// step.
internal readonly ref struct SearchKey
{
    // The most UTF-8 bytes of the text of one String part that a step writes into the scratch on its
    // stack: it bounds the copy of the text of a key compared Ordinal in a UTF-8 database, part by part, and
    // nothing else. A part whose text takes these bytes or fewer is compared as bytes with the stored text;
    // a longer one has no bytes in the scratch and is compared decoded, a piece at a time, which needs no
    // copy on the stack and gives the same result, the order of the UTF-16 code units of the two texts
    // (Comparison.ValueComparer). So a key of 256 bytes and one of 257 order and find as the oracle does,
    // also as a part of a composite key.
    public const int TextBytesPerPart = 256;

    // The scratch a search key takes at most: the null bitmap of its places, at most five, the four values
    // of a key and the bound of a range after them; five parts of 16 bytes, the widest a fixed-width value
    // is stored at (a Decimal, a Guid); the lengths of the bytes of five texts, and the bytes of five texts.
    public const int ScratchBytes = TextBytesStart + (MostPlaces * TextBytesPerPart);

    // The places a key gives at most.
    private const int MostPlaces = 5;

    // Where the stored bytes of the fixed-width parts begin in the scratch, after the null bitmap of one
    // byte, which has the bit of part i at i: each part at its width, in the order of the index, so that
    // a part lies at the offset its layout compiled (Storage.Ordered.EntryPart.FixedOffset).
    private const int FixedStart = 1;

    // Where the lengths of the UTF-8 bytes of the String parts lie in the scratch, after the fixed parts:
    // two bytes for each place, NoTextBytes for a part without bytes.
    private const int TextLengthsStart = FixedStart + (MostPlaces * 16);

    // Where the UTF-8 bytes of the String parts lie in the scratch: those of the part at place i from
    // TextBytesPerPart times i on.
    private const int TextBytesStart = TextLengthsStart + (MostPlaces * sizeof(ushort));

    // The length of a String part that has no UTF-8 bytes in the scratch.
    private const ushort NoTextBytes = ushort.MaxValue;

    private readonly IndexLayout _layout;

    // The key, bound: a value for each of its leading parts, where its caller holds it.
    private readonly ref readonly IndexKey _key;

    // The text of the bound of a range at the place after the values of the key, when the key gives one
    // and it is a String; null otherwise.
    private readonly string? _nextText;

    // The null bitmap, the stored bytes of the fixed-width parts and the bytes of the texts, in the scratch.
    private readonly ReadOnlySpan<byte> _scratch;

    // The search key of the bound key, for the entries from the identity given on, whose fixed-width
    // parts are written into the scratch, ScratchBytes at least. The key gives a value to every part of
    // the index.
    public SearchKey(IndexLayout layout, in IndexKey bound, RootHandle from, Span<byte> scratch)
    {
        var parts = layout.Parts;

        Debug.Assert(bound.Count == parts.Length && parts.Length <= MostPlaces, "A search key of a full key gives a value to every part of its index.");

        _layout = layout;
        _key = ref bound;
        From = from;
        Parts = parts.Length;
        scratch[0] = 0;

        for (var place = 0; place < parts.Length; place++)
        {
            Write(layout, parts[place], bound.ValueAt(place), place, scratch);
        }

        _scratch = scratch;
    }

    // The search key of the leading parts of the bound key, for the start or the end of a walk: its values,
    // then at the place after them the bound of its range given, or a null when nullNext is set, and then
    // the edge, before or after every entry those parts equal (KeyEdgeEnum). A null at that place serves
    // one walk: a reverse walk of a range without an upper end starts at the last entry before the nulls
    // of the part of the range, since a null is in no range.
    public SearchKey(IndexLayout layout, in IndexKey bound, scoped in IndexBound? next, bool nullNext, KeyEdgeEnum edge, Span<byte> scratch)
    {
        var parts = layout.Parts;
        var count = bound.Count;

        _layout = layout;
        _key = ref bound;
        From = edge == KeyEdgeEnum.BeforeEvery ? default : AfterEvery;
        Parts = count + (next is not null || nullNext ? 1 : 0);
        scratch[0] = 0;

        Debug.Assert(Parts <= parts.Length && Parts <= MostPlaces, "A search key gives at most a value to each part of its index, and at most five.");

        for (var place = 0; place < count; place++)
        {
            Write(layout, parts[place], bound.ValueAt(place), place, scratch);
        }

        if (next is { } given)
        {
            Write(layout, parts[count], given.Held, count, scratch);
            _nextText = given.Held.Text;
        }
        else if (nullNext)
        {
            Write(layout, parts[count], default, count, scratch);
        }

        _scratch = scratch;
    }

    // The same key with the scratch it wrote, from another identity.
    private SearchKey(scoped in SearchKey key, RootHandle from)
    {
        _layout = key._layout;
        _key = ref key._key;
        _nextText = key._nextText;
        _scratch = key._scratch;
        Parts = key.Parts;
        From = from;
    }

    // An identity above every identity a root entry has, the edge after every entry of a key: a slot is
    // below 2^31, so no handle has every bit set (RootHandle.Next).
    public static RootHandle AfterEvery { get; } = new(uint.MaxValue, uint.MaxValue);

    // The identity the key starts at: default, below every identity, for the first step of a search and for
    // the edge before every entry of a key; AfterEvery for the edge after every one.
    public RootHandle From { get; }

    // How many leading parts of the index the key gives: every part for a full key, fewer for the start or
    // the end of a walk.
    public int Parts { get; }

    // The same key, for the entries after the identity given: the next step of a search, after the entity
    // of that identity was returned or its entry was found to be a hint its root no longer holds. The
    // identities are ordered as the packed numbers of their handles (RootHandle.Next).
    public SearchKey After(RootHandle identity) => new(this, identity.Next);

    // The same key at the identity given.
    public SearchKey At(RootHandle identity) => new(this, identity);

    // Whether the part at the place given, in the order of the index, is null in the key.
    public bool IsNull(int place) => (_scratch[0] & (1 << place)) != 0;

    // The stored bytes of a fixed-width part of the index, where its layout placed them in the scratch
    // (FixedStart).
    public ReadOnlySpan<byte> FixedAt(in EntryPart part) => _scratch.Slice(FixedStart + part.FixedOffset, part.Width);

    // The number of a key of an index of one number, its one part, at FixedStart (IndexOrder).
    public long Number => MemoryMarshal.Read<long>(_scratch.Slice(FixedStart, sizeof(long)));

    // The text of a String part that is not null, as the key holds it: a value of the key, or the bound of
    // its range at the place after them.
    public ReadOnlySpan<char> TextAt(int place) => place < _key.Count ? _key.ValueAt(place).Text : _nextText;

    // The UTF-8 bytes of a String part that is not null, when the scratch holds them (see the struct):
    // false for a part compared without case, of a UTF-16 database, too long or not well-formed.
    public bool TryTextBytes(int place, out ReadOnlySpan<byte> bytes)
    {
        var length = MemoryMarshal.Read<ushort>(_scratch[(TextLengthsStart + (place * sizeof(ushort)))..]);

        if (length == NoTextBytes)
        {
            bytes = default;

            return false;
        }

        bytes = _scratch.Slice(TextBytesStart + (place * TextBytesPerPart), length);

        return true;
    }

    // Writes the value of the part at the place into the scratch: its null bit, the stored bytes of a
    // fixed-width part, zero for a null one, and the UTF-8 bytes of a String part where it has them.
    private static void Write(IndexLayout layout, in EntryPart part, in IndexValue value, int place, Span<byte> scratch)
    {
        if (value.IsNull)
        {
            scratch[0] |= (byte)(1 << place);
        }

        if (part.IsText)
        {
            WriteTextBytes(layout, part, value, place, scratch);

            return;
        }

        var at = scratch.Slice(FixedStart + part.FixedOffset, part.Width);

        if (value.IsNull)
        {
            at.Clear();
        }
        else
        {
            WriteFixed(at, part.Type, value);
        }
    }

    // Writes the value of a fixed-width part of the key, not null, in the form its stored type has, by the
    // one writer of the stored form of a value (Storage.Values.StoredScalars). The value has the field type
    // of its field, an Enum part the integer type its enum type stores (IndexValue.Bind).
    private static void WriteFixed(Span<byte> place, StoredTypeEnum type, in IndexValue value)
    {
        switch (type)
        {
            case StoredTypeEnum.Boolean:
                StoredScalars.WriteBoolean(place, value.As<bool>());
                break;
            case StoredTypeEnum.SignedInteger:
                StoredScalars.WriteInteger(place, (long)value.Integer);
                break;
            case StoredTypeEnum.UnsignedInteger:
                StoredScalars.WriteUnsigned(place, (ulong)value.Integer);
                break;
            case StoredTypeEnum.Float32:
                StoredScalars.WriteFloat32(place, value.As<float>());
                break;
            case StoredTypeEnum.Float64:
                StoredScalars.WriteFloat64(place, value.As<double>());
                break;
            case StoredTypeEnum.Decimal:
                StoredScalars.WriteDecimal(place, value.As<decimal>());
                break;
            case StoredTypeEnum.Guid:
                StoredScalars.WriteGuid(place, value.As<Guid>());
                break;
            case StoredTypeEnum.DateTimeOffset:
                StoredScalars.WriteDateTimeOffset(place, value.As<DateTimeOffset>());
                break;
            case StoredTypeEnum.DateOnly:
                StoredScalars.WriteDateOnly(place, value.As<DateOnly>());
                break;
            case StoredTypeEnum.TimeOnly:
                StoredScalars.WriteTimeOnly(place, value.As<TimeOnly>());
                break;
            case StoredTypeEnum.TimeSpan:
                StoredScalars.WriteTimeSpan(place, value.As<TimeSpan>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Not a fixed-width type.");
        }
    }

    // Writes the UTF-8 bytes of the text of a String part into its place in the scratch, and their length,
    // or NoTextBytes when it has none (see the struct).
    private static void WriteTextBytes(IndexLayout layout, in EntryPart part, in IndexValue value, int place, Span<byte> scratch)
    {
        var length = NoTextBytes;

        if (!value.IsNull && layout.Encoding == LatticeStringEncodingEnum.Utf8 && !part.Comparison!.IgnoresCase)
        {
            var destination = scratch.Slice(TextBytesStart + (place * TextBytesPerPart), TextBytesPerPart);

            if (Utf8.FromUtf16(value.Text, destination, out _, out var written, replaceInvalidSequences: false) == OperationStatus.Done)
            {
                length = (ushort)written;
            }
        }

        MemoryMarshal.Write(scratch[(TextLengthsStart + (place * sizeof(ushort)))..], length);
    }
}
