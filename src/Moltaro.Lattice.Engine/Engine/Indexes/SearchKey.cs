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
// index (Access.Sets.IndexCriteria), one for each part, and the identity it starts at, so that the first
// entry at or after it is the first entry of the key whose identity is From or later
// (docs/design/ordered-index.md, "The search by a full key").
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
// (IndexKey.Equal), so ScratchBytes always holds them.
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

    // The scratch a search key takes at most: the null bitmap of four parts, four parts of 16 bytes, the
    // widest a fixed-width value is stored at (a Decimal, a Guid), the lengths of the bytes of four texts,
    // and the bytes of four texts.
    public const int ScratchBytes = TextBytesStart + (4 * TextBytesPerPart);

    // Where the stored bytes of the fixed-width parts begin in the scratch, after the null bitmap of one
    // byte, which has the bit of part i at i: each part at its width, in the order of the index, so that
    // a part lies at the offset its layout compiled (Storage.Ordered.EntryPart.FixedOffset).
    private const int FixedStart = 1;

    // Where the lengths of the UTF-8 bytes of the String parts lie in the scratch, after the fixed parts:
    // two bytes for each place, NoTextBytes for a part without bytes.
    private const int TextLengthsStart = FixedStart + (4 * 16);

    // Where the UTF-8 bytes of the String parts lie in the scratch: those of the part at place i from
    // TextBytesPerPart times i on.
    private const int TextBytesStart = TextLengthsStart + (4 * sizeof(ushort));

    // The length of a String part that has no UTF-8 bytes in the scratch.
    private const ushort NoTextBytes = ushort.MaxValue;

    private readonly IndexLayout _layout;

    // The key, bound: a value for every part of the index, where its caller holds it.
    private readonly ref readonly IndexKey _key;

    // The null bitmap, the stored bytes of the fixed-width parts and the bytes of the texts, in the scratch.
    private readonly ReadOnlySpan<byte> _scratch;

    // The search key of the bound key, for the entries from the identity given on, whose fixed-width
    // parts are written into the scratch, ScratchBytes at least. The key gives a value to every part of
    // the index; a key of the leading parts, and a range, belong to the walk of ranges, which is not built.
    public SearchKey(IndexLayout layout, in IndexKey bound, RootHandle from, Span<byte> scratch)
    {
        var parts = layout.Parts;

        Debug.Assert(bound.Count == parts.Length && parts.Length <= 4, "A search key gives a value to every part of its index.");

        _layout = layout;
        _key = ref bound;
        From = from;
        scratch[0] = 0;

        for (var place = 0; place < parts.Length; place++)
        {
            ref readonly var part = ref parts[place];
            var value = bound.ValueAt(place);

            if (value.IsNull)
            {
                scratch[0] |= (byte)(1 << place);
            }

            if (part.IsText)
            {
                WriteTextBytes(layout, part, value, place, scratch);

                continue;
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

        _scratch = scratch;
    }

    // The search key of the leading parts of the bound key, for the start or the end of a walk: its values,
    // then at the place after them the bound of its range given, or a null when nullNext is set, and then
    // the edge, before or after every entry those parts equal (KeyEdgeEnum). A null at that place serves
    // one walk: a reverse walk of a range without an upper end starts at the last entry before the nulls
    // of the part of the range, since a null is in no range. The walk of ranges is not implemented yet.
    public SearchKey(IndexLayout layout, in IndexKey bound, scoped in IndexBound? next, bool nullNext, KeyEdgeEnum edge, Span<byte> scratch) =>
        throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // How many leading parts of the index the key gives: every part for a full key with an identity, fewer
    // for the start or the end of a walk. The walk of ranges is not implemented yet.
    public int Parts => throw new NotSupportedException("The walk of ranges is not implemented yet.");

    // The same key with the scratch it wrote, from another identity.
    private SearchKey(IndexLayout layout, ref readonly IndexKey bound, ReadOnlySpan<byte> written, RootHandle from)
    {
        _layout = layout;
        _key = ref bound;
        _scratch = written;
        From = from;
    }

    // The identity the key starts at: default, below every identity, for the first step of a search.
    public RootHandle From { get; }

    // The same key, for the entries after the identity given: the next step of a search, after the entity
    // of that identity was returned or its entry was found to be a hint its root no longer holds. The
    // identities are ordered as the packed numbers of their handles (RootHandle.Next).
    public SearchKey After(RootHandle identity) => new(_layout, in _key, _scratch, identity.Next);

    // Whether the part at the place given, in the order of the index, is null in the key.
    public bool IsNull(int place) => (_scratch[0] & (1 << place)) != 0;

    // The stored bytes of a fixed-width part of the index, where its layout placed them in the scratch
    // (FixedStart).
    public ReadOnlySpan<byte> FixedAt(in EntryPart part) => _scratch.Slice(FixedStart + part.FixedOffset, part.Width);

    // The number of a key of an index of one number, its one part, at FixedStart (IndexOrder).
    public long Number => MemoryMarshal.Read<long>(_scratch.Slice(FixedStart, sizeof(long)));

    // The text of a String part that is not null, as the key holds it.
    public ReadOnlySpan<char> TextAt(int place) => _key.ValueAt(place).Text;

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
