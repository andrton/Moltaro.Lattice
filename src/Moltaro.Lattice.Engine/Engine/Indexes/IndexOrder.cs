using System.Runtime.InteropServices;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Indexes;

// The order of the entries of one secondary index, and of a search key against them: part by part in the
// order of the index, each part by the comparison of its field, then by the identity, so that the entries
// of one key are a run ordered by the identity of their root entries. Keys are compared by their values,
// never by bytes in another order than theirs, through the one comparison service of the schema
// (Comparison.ValueComparer; decisions on queries, Q07.3.6, and on strings, Q46); a null part comes after
// every value of its part and equals another null. Equal parts need not be the same bytes, as a text under
// another case: they are one key. A text compared Ordinal in a UTF-8 database is compared by its bytes
// where they lie, the copy in the entry or the text entry of a shared key, against the key of a search
// encoded once for the step (SearchKey.TryTextBytes), with the turn that makes their order that of the
// code units (ValueComparer.CompareUtf8Ordinal); every other text is read decoded, a piece at a time on
// the stack (Storage.Values.StoredTextReader). An index of one part never null whose stored form is a
// signed integer of 8 bytes compares that number without the loop over the parts (OneNumber). The rules
// and why they hold: docs/design/ordered-index.md, "Entries and their order".
//
// No state but what it is made with. A comparison allocates nothing and reads, inside the protection of
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

        return order != 0 ? order : probe.From.Packed.CompareTo(IdentityOf(entry));
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

    // For a search inside the protection of reads: whether the root, the current state of the entity an
    // entry of the run names, has the key of the search: whether the order of its key against the key of the
    // search is zero, part by part as the order compares an entry, with the stored values of the root in
    // place of the parts of an entry. Allocates nothing.
    public bool HasKey(in RootReader root, scoped in SearchKey key)
    {
        var parts = _layout.Parts;
        var keyOffset = SearchKey.FixedStart;

        for (var place = 0; place < parts.Length; place++)
        {
            ref readonly var part = ref parts[place];
            var rootNull = root.IsNull(part.Field);
            var keyNull = key.IsNull(place);
            int order;

            if (rootNull || keyNull)
            {
                order = rootNull == keyNull ? 0 : 1;
            }
            else if (part.IsText)
            {
                order = CompareRootText(part, root, key, place);
            }
            else
            {
                order = ValueComparer.CompareStored(part.Type, root.StoredBytes(part.Field), key.FixedAt(keyOffset, part.Width));
                keyOffset += part.Width;
            }

            if (order != 0)
            {
                return false;
            }
        }

        return true;
    }

    // The order of two String parts of entries: of their bytes, for an Ordinal part of a UTF-8 database
    // whose texts lie in one place, and otherwise of their texts, a piece at a time (Comparison.ValueComparer).
    private int CompareTexts(in EntryPart part, ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (ComparesBytes(part)
            && IndexLayout.TryTextBytes(_arena, left, out var leftBytes)
            && IndexLayout.TryTextBytes(_arena, right, out var rightBytes))
        {
            return ValueComparer.CompareUtf8Ordinal(leftBytes, rightBytes);
        }

        return ValueComparer.CompareText(_layout.ReadText(_arena, left), _layout.ReadText(_arena, right), part.Comparison!);
    }

    // The order of the String part of a search key at the place given against the part of an entry: of
    // their bytes when the key has them (SearchKey.TryTextBytes) and the text of the entry lies in one place,
    // and otherwise of the stored text against the given one, turned around for the key.
    private int CompareText(in EntryPart part, scoped in SearchKey probe, int place, ReadOnlySpan<byte> entry)
    {
        if (probe.TryTextBytes(place, out var given) && IndexLayout.TryTextBytes(_arena, entry, out var stored))
        {
            return ValueComparer.CompareUtf8Ordinal(given, stored);
        }

        return -ValueComparer.CompareText(_layout.ReadText(_arena, entry), probe.TextAt(place), part.Comparison!);
    }

    // The order of the String field of a root against the part of a search key at the place given, as
    // CompareText orders a part of an entry: the text the root stores is the text its entry holds.
    private static int CompareRootText(in EntryPart part, in RootReader root, scoped in SearchKey key, int place)
    {
        var descriptor = ValueBlocks.Read(root.StoredBytes(part.Field));

        if (key.TryTextBytes(place, out var given) && !ValueBlocks.LiesInChunks(descriptor))
        {
            return ValueComparer.CompareUtf8Ordinal(root.ValueBytes(descriptor), given);
        }

        return ValueComparer.CompareText(root.ReadText(part.Field), key.TextAt(place), part.Comparison!);
    }

    // Whether a String part is compared as bytes: Ordinal in a UTF-8 database (Comparison.ValueComparer).
    private bool ComparesBytes(in EntryPart part) => _layout.Encoding == LatticeStringEncodingEnum.Utf8 && !part.Comparison!.IgnoresCase;

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
                order = CompareTexts(part, left[leftOffset..], right[rightOffset..]);
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
                order = CompareText(part, probe, place, entry[offset..]);
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
