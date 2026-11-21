using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Places one root that a child received in its arena: the reverse of BlockExport. The input holds the
// root after its type id and its last mutation sequence, which the caller has read to choose the layout
// and to take the entry of the root: its layout version, the bytes of its root payload, and the bytes of
// each value stored out of line, in the order of the descriptors, and nothing else.
//
// Everything is checked before it is used, and before anything is allocated for the root:
// - the layout version against the layout;
// - the null bitmap: no bit set beyond the nullable fields, and the bit of each nullable String or
//   Binary field against its descriptor: a field that is null has the descriptor of zeros, one that is
//   not has a descriptor of a value;
// - the kind of each descriptor against its length and the storage of its field, as RootWriter places
//   values: a text that lies in an entry of the text table (Storage.Values.StoredField.LiesInTextEntry:
//   one that is not empty of a field with Dictionary storage, one longer than RootLayout.InlineLimit of
//   a field that is part of an index) of kind Text, up to ValueBlocks.MostValueBytes bytes out of line;
//   any other value inline at most RootLayout.InlineLimit bytes, after the fixed part, in a block of its
//   own 1 to RootLayout.SingleBlockLimit bytes, in chunks more than that and at most
//   ValueBlocks.MostValueBytes; no other kind, so a Text descriptor of a value that does not lie in an
//   entry, a Text descriptor of length 0 and a text that lies in an entry of kind Inline, Block or
//   Chunked are not valid. The location of a value out of line is 0 on the wire: nothing received is
//   used as an address;
// - the total: the bytes of the input are the fixed part, the values inline and the values out of line,
//   exactly, and the payload fits one block; each value inline lies inside the payload.
// Each failure is a LatticeException with ValidationFailed, and nothing is allocated (Check). The child
// then reads the key of the root from what was checked (FixedPart, Text), so that it knows whether the
// root is the state of a key it holds before it takes anything for it (Engine.Loading.ReceivedChanges).
// Then the root block is allocated, prepared, and filled with the payload, and each value out of line is
// stored through ValueBlocks, the store the write path uses, which allocates its blocks and writes every
// header and location itself; the root block is named as their parent (Place). The bytes of a Dictionary
// text become a reference to the entry of the text table of this node that holds them, found or added
// with the seed of this node (Storage.Text.TextTable.TryAcquire), so a node stores each distinct text
// once whatever it receives. The caller publishes the root.
//
// A refusal of the arena for its budget returns the null address, and an OutOfMemoryException of the
// native allocator passes through; either way nothing of the root is left allocated. The caller decides
// what a refusal means (Engine.Loading).
//
// The arena has one changing thread at a time: the caller holds the lock of the writes of the child.
internal sealed class BlockImport
{
    private readonly Arena _arena;

    // The root payload as received, checked here before any of it reaches the arena: at most the
    // payload of one block. Reused for every root, so a checked root is valid until the next check.
    private readonly byte[] _payload = new byte[Arena.MaxBlockSize - BlockHeader.Size];

    public BlockImport(Arena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);

        _arena = arena;
    }

    // Checks the root of the layout that the rest of the input holds, and nothing more (see the class),
    // with the input advanced to its end. ValidationFailed when a length, a count or a kind is not valid;
    // nothing is dereferenced before it is checked, and nothing is allocated. The layout is that of a table
    // of the node's own schema, which a received type id selects among them (Engine.Entities.EntityTables).
    public ReceivedRoot Check(ref SequenceReader<byte> input, RootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payloadLength = ReadPayload(ref input, layout);
        var outOfLine = input.UnreadSequence;

        input.Advance(outOfLine.Length);

        return new ReceivedRoot(layout, payloadLength, outOfLine);
    }

    // A reader of the fixed part of the root checked last, which reads its fixed fields and descriptors
    // (Storage.RootReader, a reader of a copy).
    public RootReader FixedPart(in ReceivedRoot root) =>
        new(root.Layout, _payload.AsSpan(0, RootReader.FixedPartLength(root.Layout)));

    // The text of a String field of the root checked last, which is not null, as a new string decoded
    // from the encoding of the database, where the root holds it: inline in its payload, or out of line;
    // for the key of the root (Engine.Loading.ReceivedChanges), the one text of a root that is decoded on
    // receipt. ValidationFailed when it is not well-formed in that encoding, as a key received in a Delete
    // is not (Engine.Entities.PrimaryKeys.ReadKey): a key that a master stores is well-formed, since every
    // write checks its text (docs/decisions/strings.md, "Input validity and preservation"), so such a root
    // did not come from one. Every other text of a root is placed as the bytes it is and not decoded.
    public string Text(in ReceivedRoot root, LatticeField field)
    {
        var layout = root.Layout;
        ref readonly var stored = ref layout.StoredFieldOf(field);
        var descriptor = ValueBlocks.Read(_payload.AsSpan(stored.Offset - BlockHeader.Size));
        ReadOnlySequence<byte> bytes;

        if (descriptor.Kind == DescriptorKindEnum.Inline)
        {
            bytes = new ReadOnlySequence<byte>(_payload, (int)descriptor.Location - BlockHeader.Size, (int)descriptor.Length);
        }
        else
        {
            // The values out of line follow one another in the order of their descriptors.
            var before = 0L;

            for (var index = 0; index < layout.DescriptorCount; index++)
            {
                ref readonly var other = ref layout.DescriptorField(index);

                if (other.Offset == stored.Offset)
                {
                    break;
                }

                var value = ValueBlocks.Read(_payload.AsSpan(other.Offset - BlockHeader.Size));

                if (ValueBlocks.LiesOutOfLine(value))
                {
                    before += (long)value.Length;
                }
            }

            bytes = root.OutOfLine.Slice(before, (long)descriptor.Length);
        }

        if (layout.StringEncoding == LatticeStringEncodingEnum.Utf8)
        {
            try
            {
                return Protocol.StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid(layout, $"the text of the field {field.Name} is not well-formed UTF-8 ({exception.Message})");
            }
        }

        if (bytes.Length % sizeof(char) != 0)
        {
            throw Invalid(layout, $"the text of the field {field.Name} has {bytes.Length} bytes, which are no UTF-16 code units.");
        }

        var text = string.Create((int)(bytes.Length / sizeof(char)), bytes, static (units, value) => value.CopyTo(MemoryMarshal.AsBytes(units)));

        if (!WellFormedText.IsWellFormed(text))
        {
            throw Invalid(layout, $"the text of the field {field.Name} is not well-formed UTF-16: it holds a surrogate without its pair.");
        }

        return text;
    }

    // Places the root checked last for the lifetime the handle names (see the class): the address of its
    // root block, prepared; the null address when the arena refused it for its budget, with nothing of it
    // left allocated.
    public ArenaAddress Place(in ReceivedRoot root, RootHandle handle)
    {
        var layout = root.Layout;

        if (!_arena.TryAllocatePrepared(root.PayloadLength, BlockKindEnum.Root, handle, out var block))
        {
            return default;
        }

        var index = 0;
        var values = root.OutOfLine;

        try
        {
            _arena.Header(block).LayoutVersion = layout.LayoutVersion;

            var target = _arena.Payload(block);

            _payload.AsSpan(0, root.PayloadLength).CopyTo(target);

            for (; index < layout.DescriptorCount; index++)
            {
                var offset = layout.DescriptorField(index).Offset;
                var place = target.Slice(offset - BlockHeader.Size, ValueBlocks.DescriptorSize);
                var received = ValueBlocks.Read(place);

                if (!ValueBlocks.LiesOutOfLine(received))
                {
                    continue;
                }

                var length = (int)received.Length;
                var bytes = values.Slice(0, length);

                // The descriptor in place is the received one, whose location is 0; TryStore and
                // TryAcquire write the descriptor of the stored value, or of the entry, over it, or leave
                // it as it was.
                if (received.Kind == DescriptorKindEnum.Text)
                {
                    if (!_arena.Texts.TryAcquire(bytes, place, out _))
                    {
                        Abandon(block, layout, index);

                        return default;
                    }
                }
                else
                {
                    var source = ValueSource.Received(bytes);

                    if (!ValueBlocks.TryStore(_arena, handle, layout.LayoutVersion, length, ref source, place))
                    {
                        Abandon(block, layout, index);

                        return default;
                    }

                    ValueBlocks.Adopt(_arena, ValueBlocks.Read(place), block, offset);
                }

                values = values.Slice(length);
            }
        }
        catch
        {
            Abandon(block, layout, index);
            throw;
        }

        return block;
    }

    private static LatticeException Invalid(RootLayout layout, string message) =>
        new(LatticeErrorEnum.ValidationFailed, $"A root of {layout.EntityType.Name} received from the master is not valid: {message}");

    // Reads the layout version and the root payload into _payload after checking them (see the class),
    // and returns the length of the payload; the input stands at the first value out of line.
    private int ReadPayload(ref SequenceReader<byte> input, RootLayout layout)
    {
        if (!input.TryReadLittleEndian(out int version) || (uint)version != layout.LayoutVersion)
        {
            throw Invalid(layout, $"its layout version is not {layout.LayoutVersion}.");
        }

        var fixedPart = RootReader.FixedPartLength(layout);
        var total = input.Remaining;

        if (total < fixedPart)
        {
            throw Invalid(layout, $"it has {total} bytes after its layout version, fewer than the {fixedPart} of its fixed part.");
        }

        var payload = _payload.AsSpan();

        input.UnreadSequence.Slice(0, fixedPart).CopyTo(payload);
        CheckNullBitmap(layout, payload);

        var inline = 0L;
        var outOfLine = 0L;

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            ref readonly var field = ref layout.DescriptorField(index);
            var descriptor = ValueBlocks.Read(payload[(field.Offset - BlockHeader.Size)..]);
            var isNull = field.IsNullable && (payload[field.NullBit / 8] & (1 << (field.NullBit % 8))) != 0;

            if (isNull)
            {
                if (descriptor.Kind != DescriptorKindEnum.Null || descriptor.Length != 0 || descriptor.Location != 0)
                {
                    throw Invalid(layout, $"the field {field.Field.Name} is null by its bit and has the descriptor of a value.");
                }

                continue;
            }

            // The kinds a value of its length takes in its field (see the class): a text that lies in an
            // entry of the text table is out of line as a Text descriptor; any other value takes the
            // other three.
            var inEntry = field.LiesInTextEntry(descriptor.Length);

            switch (descriptor.Kind)
            {
                case DescriptorKindEnum.Inline when !inEntry && descriptor.Length <= RootLayout.InlineLimit && descriptor.Location >= (ulong)layout.FixedSize:
                    inline += descriptor.Length;
                    break;

                case DescriptorKindEnum.Block when !inEntry && descriptor.Length is > 0 and <= RootLayout.SingleBlockLimit && descriptor.Location == 0:
                case DescriptorKindEnum.Chunked when !inEntry && descriptor.Length is > RootLayout.SingleBlockLimit and <= ValueBlocks.MostValueBytes && descriptor.Location == 0:
                case DescriptorKindEnum.Text when inEntry && descriptor.Length <= ValueBlocks.MostValueBytes && descriptor.Location == 0:
                    outOfLine += descriptor.Length;
                    break;

                default:
                    throw Invalid(layout, $"the field {field.Field.Name} has a descriptor of the kind {descriptor.Kind} with the length {descriptor.Length} and the location {descriptor.Location}, which does not describe a stored value of its storage.");
            }
        }

        var payloadLength = total - outOfLine;

        if (payloadLength != fixedPart + inline || payloadLength > payload.Length)
        {
            throw Invalid(layout, $"it has {total} bytes after its layout version, where its fixed part of {fixedPart} bytes and its values describe {fixedPart + inline + outOfLine}.");
        }

        // The end of the root block, its header included, where the locations of values inline count from.
        // A received location is compared without adding the length to it, which could wrap around the
        // range of the number and pass a value that lies anywhere.
        var end = (ulong)(BlockHeader.Size + payloadLength);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            ref readonly var field = ref layout.DescriptorField(index);
            var descriptor = ValueBlocks.Read(payload[(field.Offset - BlockHeader.Size)..]);

            if (descriptor.Kind == DescriptorKindEnum.Inline && (descriptor.Location > end || descriptor.Length > end - descriptor.Location))
            {
                throw Invalid(layout, $"the value of the field {field.Field.Name} lies beyond the end of the root payload.");
            }
        }

        input.UnreadSequence.Slice(fixedPart, payloadLength - fixedPart).CopyTo(payload[fixedPart..]);
        input.Advance(payloadLength);

        return (int)payloadLength;
    }

    // No bit of the null bitmap is set beyond the nullable fields of the layout.
    private static void CheckNullBitmap(RootLayout layout, ReadOnlySpan<byte> payload)
    {
        for (var bit = layout.NullableCount; bit < layout.NullBitmapLength * 8; bit++)
        {
            if ((payload[bit / 8] & (1 << (bit % 8))) != 0)
            {
                throw Invalid(layout, $"its null bitmap sets the bit {bit}, beyond its {layout.NullableCount} nullable fields.");
            }
        }
    }

    // Frees what Place allocated and took for a root it did not complete: the values stored, and the
    // references to text entries taken, before the descriptor at the index, then the root block. The
    // descriptors from the index on still hold what was received, or the null and inline values, and
    // name no block of this arena.
    private void Abandon(ArenaAddress block, RootLayout layout, int before)
    {
        var target = _arena.Payload(block);

        for (var index = 0; index < before; index++)
        {
            RootStorage.FreeValue(_arena, ValueBlocks.Read(target[(layout.DescriptorField(index).Offset - BlockHeader.Size)..]));
        }

        _arena.Free(block);
    }
}
