using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Indexes;

// The order of the entries of one secondary index, and of a search key against them: part by part in the
// order of the index, each part by the comparison of its field, and, when every part is equal, by the
// identity, so that the entries of one key are a run ordered by the identity of their root entries
// (docs/design/ordered-index.md, "Entries and their order"). Keys are never compared as raw bytes: the
// bytes of a part are its stored value, and their order is the order of that value.
//
// A part, by its type, by the one comparison service of the schema (Comparison.ValueComparer;
// milestone contract, "Index preparation and traversal"), which this order needs beside the equality
// and the hash it serves today:
// - an integer by its value, signed or unsigned at its width; a Boolean false before true; a Float32 or
//   Float64 by its CompareTo, NaN first and the two zeros equal (decision on queries, Q07.3.6,
//   "Floating-point special values"); a Decimal by its value, so 1.0 and 1.00 are equal; a Guid by its
//   CompareTo; a DateTimeOffset, a TimeOnly and a TimeSpan by their ticks and a DateOnly by its day number,
//   each read from its stored bytes by the one reader of that form (Storage.Values.StoredScalars);
// - a String by the comparison its field declares, in the same order in a UTF-8 and in a UTF-16
//   database (Q07.3.6, "String ordering and query overrides"): Ordinal by the UTF-16 code units, and
//   OrdinalIgnoreCase by the code units of the texts each scalar value of which is replaced by its
//   mapping of the case table of the core (decision on strings, Q46), the same on every node; no .NET
//   culture or runtime table takes part. A copy is read from the entry, a shared key from its text entry,
//   a given text from the search key, a piece at a time on the stack (Storage.Values.StoredTextReader,
//   which reads from a copy it is given as well as from a descriptor), in the encoding of the database
//   the layout of the index holds;
// - a null part after every value of its part, and equal to another null, so that a composite key goes
//   on to its next part. That nulls come last in both directions of a walk is a rule of the walk.
//
// Equal parts that are not the same bytes exist: a text under another case, a Decimal of another scale.
// They are one key; the entry of a root holds the bytes of its own value (SecondaryIndex, "A write").
//
// The comparison is chosen once for an index, when its order is made from its layout: an index of one
// part never null whose stored form is a signed integer of 8 bytes compares that number, read where the
// layout places it, without the loop over the parts and the comparison by type, in the same order; every
// other index takes the loop (OneNumber).
//
// No state but what it is made with; a comparison allocates nothing and reads, inside the protection of
// reads or under the lock of the writes, only the entries it is given and the text entries they
// reference, which the node that holds them keeps from being freed.
internal sealed class IndexOrder
{
    private readonly IndexLayout _layout;

    private readonly Arena _arena;

    // Whether the index has one part, never null, stored as a signed integer of 8 bytes in the byte order
    // of the machine, whose order is the order of that number: an Int64 or an Enum over one, a
    // DateTimeOffset as its UTC ticks, a TimeOnly or a TimeSpan as its ticks (Storage.Values.StoredScalars,
    // Comparison.ValueComparer.CompareStored). Its entry is the identity and the number, with no null
    // bitmap, and a search key holds the number at FixedStart.
    private readonly bool _oneNumber;

    // The order of the entries of the layout, whose shared keys lie in the arena.
    public IndexOrder(IndexLayout layout, Arena arena)
    {
        _layout = layout;
        _arena = arena;
        _oneNumber = OneNumber(layout);
    }

    // The order of two entries of the index: below zero when the first comes first. For the writer, and
    // for a reader that descends again at a fence, which is a copy of an entry.
    public int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var order = _oneNumber ? NumberOf(left).CompareTo(NumberOf(right)) : CompareKeys(left, right);

        return order != 0 ? order : IdentityOf(left).CompareTo(IdentityOf(right));
    }

    // Whether two entries have equal keys by the order, whatever their identities and their bytes.
    public bool SameKey(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right) =>
        _oneNumber ? NumberOf(left) == NumberOf(right) : CompareKeys(left, right) == 0;

    // The order of a search key, with the identity it starts at, against an entry: below zero when the
    // key comes first. For a reader inside the protection of reads.
    public int Compare(scoped in SearchKey probe, ReadOnlySpan<byte> entry)
    {
        var order = _oneNumber ? NumberOf(probe).CompareTo(NumberOf(entry)) : CompareKey(probe, entry);

        return order != 0 ? order : IdentityOf(probe.From).CompareTo(IdentityOf(entry));
    }

    // Whether the entry has the key of the search, whatever its identity: where the run of the key ends.
    public bool SameKey(scoped in SearchKey probe, ReadOnlySpan<byte> entry) =>
        _oneNumber ? NumberOf(probe) == NumberOf(entry) : CompareKey(probe, entry) == 0;

    // Whether the two roots contribute entries of equal keys by the order, read from their stored fields
    // as the entries would hold them: a copy of a text or the text entry its root references is the text
    // the root stores, and a fixed part its stored bytes. For the writer, which tells a key that moves from
    // one equal by the order in other bytes (SecondaryIndex.ChangeOf); under the lock of the writes.
    // Allocates nothing.
    public bool SameKey(in RootReader left, in RootReader right)
    {
        foreach (ref readonly var part in _layout.Parts)
        {
            var leftNull = left.IsNull(part.Field);
            var rightNull = right.IsNull(part.Field);
            int order;

            if (leftNull || rightNull)
            {
                // A null equals a null and no value.
                order = leftNull == rightNull ? 0 : 1;
            }
            else if (part.IsText)
            {
                order = ValueComparer.CompareText(left.ReadText(part.Field), right.ReadText(part.Field), part.Comparison!);
            }
            else
            {
                order = ValueComparer.CompareStored(part.Type, left.StoredBytes(part.Field), right.StoredBytes(part.Field));
            }

            if (order != 0)
            {
                return false;
            }
        }

        return true;
    }

    // Whether the layout is that of an index of one number (_oneNumber).
    private static bool OneNumber(IndexLayout layout)
    {
        var parts = layout.Parts;

        if (parts.Length != 1 || parts[0].NullBit != EntryPart.NoNullBit || parts[0].Width != sizeof(long))
        {
            return false;
        }

        return parts[0].Type is StoredTypeEnum.SignedInteger or StoredTypeEnum.DateTimeOffset or StoredTypeEnum.TimeOnly or StoredTypeEnum.TimeSpan;
    }

    // The number of an entry of an index of one number, after its identity.
    private static long NumberOf(ReadOnlySpan<byte> entry) => MemoryMarshal.Read<long>(entry[IndexLayout.IdentityBytes..]);

    // The number of a search key of an index of one number, in its scratch.
    private static long NumberOf(scoped in SearchKey probe) => MemoryMarshal.Read<long>(probe.FixedAt(SearchKey.FixedStart, sizeof(long)));

    // The identity of an entry as the number the order compares: the RootHandle it begins with.
    private static ulong IdentityOf(ReadOnlySpan<byte> entry) => MemoryMarshal.Read<ulong>(entry);

    private static ulong IdentityOf(RootHandle handle) => ((ulong)handle.Generation << 32) | handle.Slot;

    // The parts of two entries, one after the other (see the class).
    private int CompareKeys(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var leftOffset = IndexLayout.IdentityBytes + _layout.NullBitmapBytes;
        var rightOffset = leftOffset;

        foreach (ref readonly var part in _layout.Parts)
        {
            var leftNull = _layout.IsNull(left, part);
            var rightNull = _layout.IsNull(right, part);
            int order;

            if (leftNull || rightNull)
            {
                // A null after every value, and equal to a null.
                order = leftNull == rightNull ? 0 : leftNull ? 1 : -1;
            }
            else if (part.IsText)
            {
                order = ValueComparer.CompareText(_layout.ReadText(_arena, left[leftOffset..]), _layout.ReadText(_arena, right[rightOffset..]), part.Comparison!);
            }
            else
            {
                order = ValueComparer.CompareStored(part.Type, left.Slice(leftOffset, part.Width), right.Slice(rightOffset, part.Width));
            }

            if (order != 0)
            {
                return order;
            }

            leftOffset += part.IsText ? IndexLayout.TextPartBytes(left[leftOffset]) : part.Width;
            rightOffset += part.IsText ? IndexLayout.TextPartBytes(right[rightOffset]) : part.Width;
        }

        return 0;
    }

    // The parts of a search key against those of an entry (see the class): the fixed parts of the key
    // are stored values in its scratch, its texts as they were given.
    private int CompareKey(scoped in SearchKey probe, ReadOnlySpan<byte> entry)
    {
        var parts = _layout.Parts;
        var offset = IndexLayout.IdentityBytes + _layout.NullBitmapBytes;
        var probeOffset = SearchKey.FixedStart;

        for (var place = 0; place < parts.Length; place++)
        {
            ref readonly var part = ref parts[place];
            var probeNull = probe.IsNull(place);
            var entryNull = _layout.IsNull(entry, part);
            int order;

            if (probeNull || entryNull)
            {
                order = probeNull == entryNull ? 0 : probeNull ? 1 : -1;
            }
            else if (part.IsText)
            {
                // The order of the stored text against the given one, turned around for the key.
                order = -ValueComparer.CompareText(_layout.ReadText(_arena, entry[offset..]), probe.TextAt(place), part.Comparison!);
            }
            else
            {
                order = ValueComparer.CompareStored(part.Type, probe.FixedAt(probeOffset, part.Width), entry.Slice(offset, part.Width));
            }

            if (order != 0)
            {
                return order;
            }

            if (part.IsText)
            {
                offset += IndexLayout.TextPartBytes(entry[offset]);
            }
            else
            {
                offset += part.Width;
                probeOffset += part.Width;
            }
        }

        return 0;
    }
}
