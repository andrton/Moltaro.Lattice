using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Ordered;

// How the entries and the nodes of one secondary index lie in arena memory (docs/design/ordered-index.md,
// "Nodes in the arena"). Compiled once, when the database opens, from the declaration of the index and
// the root layout of its entity type, and never changed afterwards, so one layout serves the writer and
// every reader at once, as RootLayout does. It knows bytes only; the order of entries is
// Engine.Indexes.IndexOrder's.
//
// An entry, in this order:
// - the identity of the entity: the RootHandle of its root entry, 8 bytes in the byte order of the
//   machine. One root entry contributes one entry to each index, so an entry is unique by its key and
//   its identity, and the entries of one key are a run ordered by identity;
// - the null bitmap: one bit for each part that can be null, NullBitmapBytes bytes;
// - the parts, in the order of the index. A part of a fixed-width type is the bytes its root stores
//   for it, at the width the root gives it (RootLayout); a null part keeps its width and is zero. A
//   String part is one head byte and what the head says: 0 to TextCopyLimit, a copy of that many bytes of
//   the stored text, in the encoding of the database, whatever the storage mode of the field (decision on
//   index structures, "Search-key ownership", the note of 19 November 2026); SharedTextHead, then the
//   length (4 bytes) and the relative address (8) of the text entry the root references for a longer
//   value. A field that is part of an index stores a value longer than TextCopyLimit as a text entry,
//   whatever its storage mode, so a longer value always has one. A null String part is the head 0 with
//   its null bit set.
// An index without a String part has entries of one width, FixedEntryBytes, which lie in an array; an
// entry of an index with one has the width of its values and lies behind an offset. An entry is never
// written after the node that holds it is published.
//
// The node: one block of the arena of kind IndexNode and NodeBytes bytes, its payload NodeHeader, then
// for a leaf the entries, for an inner node the Count + 1 children, relative addresses of 8 bytes on
// 8-byte boundaries, then the Count separators; in a node of an index with a String part the entries or
// the separators follow an array of their offsets from the start of the payload, 2 bytes each, in order.
// Separator i is a copy of an entry: every entry below child i is below it, every entry below child
// i + 1 at or above it. A node holds no fence of its own: the fences of a node are the separators of its
// parent beside it, which a descent passes (ordered-index.md, "Fences").
internal sealed class IndexLayout
{
    // The bytes of a node, header of the block included, unless an index needs more for
    // LeastEntriesPerNode of its widest entries: the size class of 1,024 bytes, the last of the
    // 16-byte steps, so no slack of a class is lost. On the prototype of the structure
    // (src/Prototypes/SecondaryIndexComparison, Ordered, one run of five seconds a case, an Int64 key):
    // an exact search 1.90 M a second on one thread and 7.32 M on sixteen with nodes of 1 KiB, 2.15 M and
    // 8.26 M with nodes of 4 KiB, a difference near the tenth one run does not decide; a write copies one
    // node of each index it changes and retires the old one, so a node of 4 KiB copies and holds retired
    // four times the bytes for each write, beside readers. 18.8, 25.1 and 38.6 bytes an entry for keys of
    // 4, 8 and 16 bytes inserted in random order, at 1 KiB. Measured again in the engine.
    public const int SmallestNodeBytes = 1024;

    // The most bytes of a String value an entry holds a copy of: the most a root holds inline, the same
    // number by the decision on index structures (the note of 19 November 2026), so an indexed value is
    // copied exactly when it could lie in its root, and shared exactly when it lies in a text entry.
    public const int TextCopyLimit = RootLayout.InlineLimit;

    // The head of a String part that references a text entry. The head 254 is kept for a shared key that
    // carries its first bytes beside the reference, which the measurement of ordered-index.md, "Text
    // keys", decides; no other head above TextCopyLimit is written.
    public const byte SharedTextHead = 255;

    // The bytes of a String part that references a text entry: the head, the length, the address.
    public const int SharedTextBytes = 1 + sizeof(uint) + sizeof(ulong);

    // The bytes of the identity that begins every entry.
    public const int IdentityBytes = sizeof(ulong);

    // The bytes of an offset, and of a child address, in a node.
    public const int OffsetBytes = sizeof(ushort);

    public const int ChildBytes = sizeof(ulong);

    // The fewest of its widest entries a leaf holds, and of its widest separators an inner node holds:
    // what makes a split of a full node into two always fit (ordered-index.md, "Writes").
    public const int LeastEntriesPerNode = 4;

    private readonly EntryPart[] _parts;

    private IndexLayout(LatticeIndex index, EntryPart[] parts, int nullBitmapBytes, int fixedEntryBytes, int widestEntryBytes, int nodeBytes, int leastSeparators, LatticeStringEncodingEnum encoding)
    {
        Index = index;
        _parts = parts;
        NullBitmapBytes = nullBitmapBytes;
        FixedEntryBytes = fixedEntryBytes;
        WidestEntryBytes = widestEntryBytes;
        NodeBytes = nodeBytes;
        LeastSeparators = leastSeparators;
        Encoding = encoding;
        TextParts = parts.Count(part => part.IsText);
    }

    // The index the layout was compiled for.
    public LatticeIndex Index { get; }

    // The parts of an entry, in the order of the index.
    public ReadOnlySpan<EntryPart> Parts => _parts;

    // The bytes of the null bitmap of an entry: one bit for each part that can be null.
    public int NullBitmapBytes { get; }

    // The bytes of every entry of an index without a String part; 0 for an index with one.
    public int FixedEntryBytes { get; }

    // Whether the index has a String part, and so entries of their own widths, behind offsets, some of
    // which may reference text entries.
    public bool HasText => FixedEntryBytes == 0;

    // The bytes of the widest entry the index can have: every String part a copy of TextCopyLimit bytes.
    public int WidestEntryBytes { get; }

    // The bytes of a node of this index, header of the block included: SmallestNodeBytes, or the
    // smallest size class above it that holds LeastEntriesPerNode of the widest entries in a leaf and of
    // the widest separators, with their children, in an inner node.
    public int NodeBytes { get; }

    // The bytes of the payload of a node: NodeHeader, then the children, the offsets and the entries.
    public int PayloadBytes => NodeBytes - BlockHeader.Size;

    // The fewest separators an inner node holds before it is full: as many of the widest as fit. A root
    // made by a split holds one, and gains at most one for each insert, which bounds how far the tree
    // can grow while writes are prepared (docs/design/ordered-index.md, "The reservation").
    public int LeastSeparators { get; }

    // How many parts are Strings: the most text entries one entry can reference.
    public int TextParts { get; }

    // The encoding of the database, in which a String part holds its copy.
    public LatticeStringEncodingEnum Encoding { get; }

    // Compiles the layout of a declared index of the entity type whose root layout is given. The parts
    // come from the root layout: the stored type, width and place of each field. An index whose
    // LeastEntriesPerNode widest entries do not fit the largest block, Arena.MaxBlockSize, is refused with
    // LatticeException, InvalidSchema, naming the index, before any data is admitted, as RootLayout refuses
    // a fixed part beyond one block: a rule of the schema language, which LatticeIndex states with the
    // widths, the same on every node. 251 String parts that are never null fit; 252 do not.
    public static IndexLayout Compile(LatticeIndex index, RootLayout root)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(root);

        var parts = new EntryPart[index.Parts.Count];
        var nullable = 0;
        var fixedBytes = 0;
        var texts = 0;

        for (var place = 0; place < parts.Length; place++)
        {
            var part = index.Parts[place];
            ref readonly var stored = ref root.StoredFieldOf(part.Field);
            var isText = stored.Type == StoredTypeEnum.String;
            var nullBit = part.CanBeNull ? nullable++ : EntryPart.NoNullBit;

            parts[place] = new EntryPart(part.Field, stored.Type, isText ? 0 : stored.Width, nullBit, isText ? 0 : fixedBytes, isText ? part.Field.Declaration.TextComparison : null);

            if (isText)
            {
                texts++;
            }
            else
            {
                fixedBytes += stored.Width;
            }
        }

        var nullBitmapBytes = (nullable + 7) / 8;
        var fixedEntryBytes = IdentityBytes + nullBitmapBytes + fixedBytes;
        var widestEntryBytes = fixedEntryBytes + (texts * (1 + TextCopyLimit));
        var offset = texts > 0 ? OffsetBytes : 0;

        // Four of the widest entries in a leaf, and four of the widest separators with their five children
        // in an inner node.
        var leaf = NodeHeader.Size + (LeastEntriesPerNode * (long)(offset + widestEntryBytes));
        var inner = leaf + ((LeastEntriesPerNode + 1L) * ChildBytes);

        if (inner > Arena.MaxBlockSize - BlockHeader.Size)
        {
            throw new LatticeException(
                LatticeErrorEnum.InvalidSchema,
                $"The index {index.Name} of the entity type {root.EntityType.Name} has entries of up to {widestEntryBytes} bytes: four of them take {inner} bytes of a node, more than the {Arena.MaxBlockSize - BlockHeader.Size} bytes of the payload of one block.");
        }

        var nodeBytes = inner + BlockHeader.Size <= SmallestNodeBytes ? SmallestNodeBytes : Arena.BlockSizeFor((int)inner);
        var leastSeparators = (nodeBytes - BlockHeader.Size - NodeHeader.Size - ChildBytes) / (ChildBytes + offset + widestEntryBytes);

        return new IndexLayout(index, parts, nullBitmapBytes, texts > 0 ? 0 : fixedEntryBytes, widestEntryBytes, nodeBytes, leastSeparators, root.StringEncoding);
    }

    // The identity an entry begins with.
    public static RootHandle IdentityOf(ReadOnlySpan<byte> entry) => MemoryMarshal.Read<RootHandle>(entry);

    // Whether the part is null in the entry.
    public bool IsNull(ReadOnlySpan<byte> entry, in EntryPart part) =>
        part.NullBit != EntryPart.NoNullBit && (entry[IdentityBytes + (part.NullBit / 8)] & (1 << (part.NullBit % 8))) != 0;

    // The bytes of a String part that begins with the head given: the head and a copy, or the head and a
    // reference to a text entry.
    public static int TextPartBytes(byte head) => head == SharedTextHead ? SharedTextBytes : 1 + head;

    // The text of a String part that is not null, which begins the span, to be read a piece at a time
    // (Values.StoredTextReader): its copy, or the text entry it references, which the node that holds the
    // part keeps from being freed. Inside the protection of reads or under the lock of the writes.
    public StoredTextReader ReadText(Arena arena, ReadOnlySpan<byte> part)
    {
        var head = part[0];

        return head == SharedTextHead
            ? new StoredTextReader(arena, SharedDescriptorOf(part), default, Encoding)
            : new StoredTextReader(part.Slice(1, head), Encoding);
    }

    // The bytes of a String part that is not null, which begins the span, in the encoding of the database,
    // when they lie in one place: the copy of the part, or the text entry it references when that is not in
    // chunks; what an Ordinal order of a UTF-8 database compares as bytes. False for a text in chunks, which
    // is read a piece at a time (ReadText). Inside the protection of reads or under the lock of the writes.
    public static bool TryTextBytes(Arena arena, ReadOnlySpan<byte> part, out ReadOnlySpan<byte> bytes)
    {
        var head = part[0];

        if (head != SharedTextHead)
        {
            bytes = part.Slice(1, head);

            return true;
        }

        var descriptor = SharedDescriptorOf(part);

        if (ValueBlocks.LiesInChunks(descriptor))
        {
            bytes = default;

            return false;
        }

        bytes = ValueBlocks.Contiguous(arena, descriptor, default);

        return true;
    }

    // Writes the entry a root contributes to the index, under the identity of its root entry, into the
    // destination, which has WidestEntryBytes at least, and returns its bytes. A fixed part is copied from
    // the root as its bytes lie, not written again from its value: the stored form of a value is written
    // in one place, Values.StoredScalars, which the root writer uses. A String part of TextCopyLimit bytes
    // or less is copied from wherever its value lies, the root or the text entry of a Dictionary field,
    // and a longer one references the text entry the root's descriptor names. Allocates nothing. For the
    // writer under the lock of the writes, and for a reader inside the protection of reads that checks a
    // root against a key.
    public int WriteEntry(in RootReader root, RootHandle identity, Span<byte> destination)
    {
        MemoryMarshal.Write(destination, in identity);

        var bitmap = destination.Slice(IdentityBytes, NullBitmapBytes);
        var length = IdentityBytes + NullBitmapBytes;

        bitmap.Clear();

        foreach (ref readonly var part in Parts)
        {
            if (root.IsNull(part.Field))
            {
                bitmap[part.NullBit / 8] |= (byte)(1 << (part.NullBit % 8));

                // A null fixed part keeps its width and is zero; a null text is the head 0.
                var width = part.IsText ? 1 : part.Width;

                destination.Slice(length, width).Clear();
                length += width;
            }
            else if (part.IsText)
            {
                length += WriteText(root, part.Field, destination[length..]);
            }
            else
            {
                root.StoredBytes(part.Field).CopyTo(destination[length..]);
                length += part.Width;
            }
        }

        return length;
    }

    // Whether the two roots contribute entries of the same bytes under one identity, decided from their
    // stored fields without writing the entries: what WriteEntry would write, part by part. A part null in
    // both is the same, whatever bytes lie under the null, since an entry writes zeros there; null in one
    // only differs by the null bitmap. A fixed part is the same when its stored bytes are, which is not
    // equality by value: a Decimal of another scale, -0 and +0 or NaNs of other bits differ, and a
    // DateTimeOffset stores its UTC ticks, so an offset changed alone does not. A String part of
    // TextCopyLimit bytes or less is the same when its text is the same bytes, wherever they lie, never by
    // its descriptor, whose offset depends on the root; a longer one when it references the same text
    // entry, as the entry holds the length and the address. For the writer, under the lock of the writes.
    // Allocates nothing.
    public bool SameEntry(in RootReader left, in RootReader right)
    {
        foreach (ref readonly var part in Parts)
        {
            var leftNull = left.IsNull(part.Field);
            var rightNull = right.IsNull(part.Field);

            if (leftNull || rightNull)
            {
                if (leftNull != rightNull)
                {
                    return false;
                }
            }
            else if (part.IsText)
            {
                if (!SameText(left, right, part.Field))
                {
                    return false;
                }
            }
            else if (!left.StoredBytes(part.Field).SequenceEqual(right.StoredBytes(part.Field)))
            {
                return false;
            }
        }

        return true;
    }

    // The descriptors of the text entries the String parts of an entry reference, its shared keys, into
    // the span, which has a place for each String part, and how many there are, as a root names them: for
    // the count of a node, which holds one reference for each shared key among its entries or separators
    // from the moment it is written until it is freed (Text.TextTable; ordered-index.md, "Text
    // references"), for the text entries an insert publishes before the node that holds its entry, and for
    // the bytes of their slots, which a retired node counts toward the next look of the drain, as a retired
    // root counts the text entries it names (Values.ValueBlocks.SlotBytesOf).
    public int SharedTexts(ReadOnlySpan<byte> entry, Span<ValueDescriptor> texts)
    {
        if (!HasText)
        {
            return 0;
        }

        var count = 0;
        var offset = IdentityBytes + NullBitmapBytes;

        foreach (ref readonly var part in Parts)
        {
            if (!part.IsText)
            {
                offset += part.Width;

                continue;
            }

            if (entry[offset] == SharedTextHead)
            {
                texts[count++] = SharedDescriptorOf(entry[offset..]);
            }

            offset += TextPartBytes(entry[offset]);
        }

        return count;
    }

    // For the tests of the engine (EngineDatabase.IndexNodes): the value of each part of an entry as a
    // record of the entity type holds it (LatticeValueDeclaration.ValueType), into the array, which has a
    // place for each part: null, a scalar boxed in its exact type, an Enum as the integer of its underlying
    // type, or a new string read from the copy or from the text entry. Under the lock of the writes.
    public void ReadValues(Arena arena, ReadOnlySpan<byte> entry, object?[] values)
    {
        var offset = IdentityBytes + NullBitmapBytes;

        for (var place = 0; place < _parts.Length; place++)
        {
            ref readonly var part = ref _parts[place];
            var bytes = part.IsText ? TextPartBytes(entry[offset]) : part.Width;
            var value = entry.Slice(offset, bytes);

            values[place] = IsNull(entry, part) ? null : part.IsText ? TextOf(arena, value) : ScalarOf(part.Field, value);
            offset += bytes;
        }
    }

    // The descriptor of the text entry a shared String part references: the kind Text, its length and
    // its address, as a root names it.
    private static ValueDescriptor SharedDescriptorOf(ReadOnlySpan<byte> part) => new()
    {
        Kind = DescriptorKindEnum.Text,
        Length = MemoryMarshal.Read<uint>(part[1..]),
        Location = MemoryMarshal.Read<ulong>(part[(1 + sizeof(uint))..]),
    };

    // Writes the String part of a value that is not null, and returns its bytes: a copy of a text of
    // TextCopyLimit bytes or less from where it lies, or a reference to the text entry of a longer one,
    // which the layout of the root places in an entry (StoredField.LiesInTextEntry).
    private static int WriteText(in RootReader root, LatticeField field, Span<byte> destination)
    {
        var descriptor = ValueBlocks.Read(root.StoredBytes(field));

        if (descriptor.Length <= TextCopyLimit)
        {
            destination[0] = (byte)descriptor.Length;
            root.ValueBytes(descriptor).CopyTo(destination[1..]);

            return 1 + (int)descriptor.Length;
        }

        Debug.Assert(descriptor.Kind == DescriptorKindEnum.Text, "An indexed text longer than a copy lies in a text entry.");

        destination[0] = SharedTextHead;
        MemoryMarshal.Write(destination[1..], descriptor.Length);
        MemoryMarshal.Write(destination[(1 + sizeof(uint))..], descriptor.Location);

        return SharedTextBytes;
    }

    // Whether the String field, not null in either root, gives the same String part in an entry of each
    // (WriteText): the same copy, or the same reference to a text entry.
    private static bool SameText(in RootReader left, in RootReader right, LatticeField field)
    {
        var leftText = ValueBlocks.Read(left.StoredBytes(field));
        var rightText = ValueBlocks.Read(right.StoredBytes(field));

        if (leftText.Length != rightText.Length)
        {
            return false;
        }

        return leftText.Length <= TextCopyLimit
            ? left.ValueBytes(leftText).SequenceEqual(right.ValueBytes(rightText))
            : leftText.Location == rightText.Location;
    }

    private string TextOf(Arena arena, ReadOnlySpan<byte> part)
    {
        var reader = ReadText(arena, part);
        var text = new StringBuilder();
        Span<char> piece = stackalloc char[256];

        for (var length = reader.Read(piece); length > 0; length = reader.Read(piece))
        {
            text.Append(piece[..length]);
        }

        return text.ToString();
    }

    private static object ScalarOf(LatticeField field, ReadOnlySpan<byte> value) => field.Declaration.ScalarType switch
    {
        LatticeFieldTypeEnum.Boolean => StoredScalars.ReadBoolean(value),
        LatticeFieldTypeEnum.Int8 => (sbyte)StoredScalars.ReadInteger(value),
        LatticeFieldTypeEnum.Int16 => (short)StoredScalars.ReadInteger(value),
        LatticeFieldTypeEnum.Int32 => (int)StoredScalars.ReadInteger(value),
        LatticeFieldTypeEnum.Int64 => StoredScalars.ReadInteger(value),
        LatticeFieldTypeEnum.UInt8 => (byte)StoredScalars.ReadUnsigned(value),
        LatticeFieldTypeEnum.UInt16 => (ushort)StoredScalars.ReadUnsigned(value),
        LatticeFieldTypeEnum.UInt32 => (uint)StoredScalars.ReadUnsigned(value),
        LatticeFieldTypeEnum.UInt64 => StoredScalars.ReadUnsigned(value),
        LatticeFieldTypeEnum.Float32 => StoredScalars.ReadFloat32(value),
        LatticeFieldTypeEnum.Float64 => StoredScalars.ReadFloat64(value),
        LatticeFieldTypeEnum.Decimal => StoredScalars.ReadDecimal(value),
        LatticeFieldTypeEnum.Guid => StoredScalars.ReadGuid(value),
        LatticeFieldTypeEnum.DateTimeOffset => StoredScalars.ReadDateTimeOffset(value),
        LatticeFieldTypeEnum.DateOnly => StoredScalars.ReadDateOnly(value),
        LatticeFieldTypeEnum.TimeOnly => StoredScalars.ReadTimeOnly(value),
        _ => StoredScalars.ReadTimeSpan(value),
    };
}
