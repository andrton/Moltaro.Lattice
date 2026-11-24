using System.Buffers;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The block that a writer fills with structures and with the values their descriptors name inline, and the
// one placement of a String, a Binary and a packed List into it or out of it: the image of a root that its
// writer collects before the root block exists (RootWriter), the root block of a root a node received
// (Replication.Load.BlockImport), or a block of the records of a List (NestedValues). It holds the bytes of the
// block, the room left in it for values inline, the arena and the owner of the blocks that the values out of
// line take, and what the writing has taken so far. A ref struct on the stack of one write, passed by
// reference: placing a value changes it.
//
// The rules of a placement. A value above the bound of one value, ValueBlocks.MostValueBytes, and text that is
// not well-formed are refused first with ValidationFailed and the field named, before anything is allocated:
// the guard of the stored form for whoever writes inside the engine, by the same rule and bound as the
// validation of the core (Access.Values.ValueBound, WellFormedText). Text whose place says so lies in an entry
// of the text table, which the root takes one reference to. Otherwise a value of at most
// ValueBlocks.InlineLimit bytes lies inline when the block has room, which a root image and a block of records
// always have, since each is made with that room for each of its descriptors; a larger one lies in blocks of
// its own (ValueBlocks.TryStore), whose parent is the block that holds the descriptor. Once the arena refused a
// block, nothing more is allocated: Refused is set, and the write is refused for its memory. A value is placed
// whole or not at all, so a descriptor names a whole value or nothing; an OutOfMemoryException of the native
// allocator passes with nothing of the value taken. For a root for a forwarded write, a value out of line is
// kept apart (ExportedValues) instead of in blocks. A block that holds what it received has no room left: every
// value placed into it lies out of line, as it came (InlineEnd at End).
internal ref struct ValueHolder
{
    // A holder of the bytes given, which lie at the offset origin of the block, its header counted, so that
    // the byte at the offset o of the block is bytes[o - origin]; the first value inline goes at the offset
    // inlineEnd. The address is that of the block, which every block of a value out of line names as its
    // parent; the null address for the image of a root, whose writer names the root block as that parent
    // when it completes the root (RootWriter.Complete).
    public ValueHolder(Arena arena, RootLayout layout, RootHandle root, Span<byte> bytes, int origin, ArenaAddress address, int inlineEnd)
    {
        Arena = arena;
        Layout = layout;
        Root = root;
        Bytes = bytes;
        Origin = origin;
        Address = address;
        InlineEnd = inlineEnd;
    }

    // The arena of the blocks; null for a root for a forwarded write, which allocates nothing, and for a holder
    // of one packed record, which holds no descriptor (PackedOwnedValues).
    public Arena Arena { get; }

    // The layout of the root the values belong to: its version, which the headers of their blocks carry, the
    // encoding of its text and the name of its entity type; null for a holder of one packed record.
    public RootLayout Layout { get; }

    // The root that every block the writing takes belongs to.
    public RootHandle Root { get; }

    // The encoding of the database, in which text is stored.
    public readonly LatticeStringEncodingEnum Encoding => Layout.StringEncoding;

    // The bytes of the block from the offset Origin on.
    public Span<byte> Bytes { get; }

    public int Origin { get; }

    // The block, or the null address for the image of a root.
    public ArenaAddress Address { get; }

    // The values out of line of a root for a forwarded write, or of a block of its records, kept by their
    // descriptors; null for a block of an arena.
    public ExportedValues? Exported { get; init; }

    // The offset of the block, header counted, where the next value inline goes; the room for values inline
    // ends with Bytes.
    public int InlineEnd { get; set; }

    // The end of the room, as InlineEnd counts.
    public readonly int End => Origin + Bytes.Length;

    // The bytes of the slots of the blocks the writing took, and of the entries of the text table it added.
    public long SlotBytes { get; set; }

    // The arena refused a block: nothing more is allocated, and the write is refused for its memory.
    public bool Refused { get; set; }

    // The bytes of the block at the offset given, header counted, for the length given.
    public readonly Span<byte> At(int offset, int length) => Bytes.Slice(offset - Origin, length);

    // Takes a prepared block for this root and accounts for its whole slot. The caller makes
    // the initialized block reachable through its parent descriptor or chunk address.
    public bool TryAllocate(int length, BlockKindEnum kind, ArenaAddress parent, int offset, out ArenaAddress address)
    {
        if (!ValueBlocks.TryAllocate(Arena, length, kind, Root, Layout.LayoutVersion, parent, (uint)offset, out address))
        {
            return false;
        }

        SlotBytes += Arena.BlockSizeFor(length);
        return true;
    }

    // Places a text of the place, its descriptor at the offset given (see the type). The whole text is checked
    // before anything is placed: a lone surrogate at its end must not be found after its first chunks were
    // allocated. Checking, counting and, in a UTF-8 database, encoding are three passes over the text, kept
    // apart on purpose: the decision on input validity (docs/decisions/strings.md, "Input validity and
    // preservation") asks to combine them where possible, and that is done when a measurement shows it is
    // worth the code.
    public void PlaceText(in StoredField place, int offset, ReadOnlySpan<char> text)
    {
        // A code unit takes one byte at least in UTF-8 and two in UTF-16, so text of more code units than the
        // bound has bytes is above it in either encoding: it is refused before it is scanned, and the text that
        // is counted below has an encoded length that fits an int.
        if (text.Length > ValueBlocks.MostValueBytes)
        {
            throw AboveTheBound(place);
        }

        if (!WellFormedText.IsWellFormed(text))
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The text for the field {Name(place)} is not well-formed: it has a surrogate without its pair.");
        }

        var length = StoredText.EncodedLength(text, Encoding);

        if (place.LiesInTextEntry(length))
        {
            PlaceInTextEntry(place, offset, text, length);

            return;
        }

        var source = ValueSource.Text(text, Encoding);

        PlaceBytes(place, offset, length, ref source);
    }

    // Places a value of length bytes of the place, which the source hands out, its descriptor at the offset
    // given: inline when it is short enough and the block has room, else in blocks of its own (see the type).
    public void PlaceBytes<TSource>(in StoredField place, int offset, int length, ref TSource source, bool allowInline = true)
        where TSource : IValueSource, allows ref struct
    {
        if (length > ValueBlocks.MostValueBytes)
        {
            throw AboveTheBound(place);
        }

        if (Refused)
        {
            return;
        }

        var descriptor = At(offset, ValueBlocks.DescriptorSize);

        if (allowInline && length <= ValueBlocks.InlineLimit && InlineEnd + length <= End)
        {
            source.Fill(At(InlineEnd, length));
            ValueBlocks.Write(descriptor, new ValueDescriptor { Kind = DescriptorKindEnum.Inline, Length = (uint)length, Location = (ulong)InlineEnd });
            InlineEnd += length;
        }
        else if (Exported is not null)
        {
            // Kept apart in the order of its descriptor, and described as the blocks that would hold it.
            Exported.Store(offset, length, ref source);
            ValueBlocks.Write(descriptor, new ValueDescriptor
            {
                Kind = length <= ValueBlocks.SingleBlockLimit ? DescriptorKindEnum.Block : DescriptorKindEnum.Chunked,
                Length = (uint)length,
                Location = 0,
            });
        }
        else if (ValueBlocks.TryStore(Arena, Root, Layout.LayoutVersion, length, ref source, descriptor))
        {
            SlotBytes += ValueBlocks.SlotBytesOf(length);
            AdoptIfPlaced(ValueBlocks.Read(descriptor), offset);
        }
        else
        {
            Refused = true;
        }
    }

    // Names the block of the holder as the parent of the blocks of a value placed out of line, at the offset of
    // its descriptor; the image of a root has no block yet, and its writer names the root block later.
    public readonly void AdoptIfPlaced(in ValueDescriptor descriptor, int offset)
    {
        if (!Address.IsNull)
        {
            ValueBlocks.Adopt(Arena, descriptor, Address, offset);
        }
    }

    // Places a text that lies in an entry of the text table, length bytes in the encoding of the database, at
    // least one: the descriptor of kind Text names the entry that holds these bytes, which the root takes one
    // reference to (Text.TextTable.TryAcquire), or nothing when the arena refused a block before or refuses a
    // new entry now. A new entry counts in SlotBytes, as the blocks of a value do: the write allocated it. For a
    // forwarded write the bytes are kept apart, as the wire carries them: a Text descriptor with location 0.
    private void PlaceInTextEntry(in StoredField place, int offset, ReadOnlySpan<char> text, int length)
    {
        if (length > ValueBlocks.MostValueBytes)
        {
            throw AboveTheBound(place);
        }

        if (Refused)
        {
            return;
        }

        var descriptor = At(offset, ValueBlocks.DescriptorSize);

        if (Exported is not null)
        {
            var source = ValueSource.Text(text, Encoding);

            Exported.Store(offset, length, ref source);
            ValueBlocks.Write(descriptor, new ValueDescriptor { Kind = DescriptorKindEnum.Text, Length = (uint)length, Location = 0 });
        }
        else if (Arena.Texts.TryAcquire(text, Encoding, length, descriptor, out var newBytes))
        {
            SlotBytes += newBytes;
        }
        else
        {
            Refused = true;
        }
    }

    // Places a text of the place that lies in an entry of the text table, received as the bytes of its
    // encoding, its descriptor at the offset given: the entry that holds these bytes, found or added, of which
    // the block takes one reference, or nothing when the arena refused a block before or refuses a new entry
    // now (Text.TextTable.TryAcquire). The bytes were checked on receipt (Replication.Load.BlockImport).
    public void PlaceEntry(int offset, in ReadOnlySequence<byte> bytes)
    {
        if (Refused)
        {
            return;
        }

        if (Arena.Texts.TryAcquire(bytes, At(offset, ValueBlocks.DescriptorSize), out var newBytes))
        {
            SlotBytes += newBytes;
        }
        else
        {
            Refused = true;
        }
    }

    private readonly string Name(in StoredField place) => $"{Layout.EntityType.Name}.{place.Field.Name}";

    // The refusal of a value above the bound of one value.
    private readonly LatticeException AboveTheBound(in StoredField place) =>
        new(LatticeErrorEnum.ValidationFailed, $"The value for the field {Name(place)} takes more than the {ValueBlocks.MostValueBytes} bytes one value may take.");
}
