using System.Buffers;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Places one root that a child received in its arena (brief of Epic 1, sprint S10; the planner's
// decision of 17 November 2026 after the study, point 2): the reverse of BlockExport. The input holds the
// root after its type id and its last mutation sequence, which the caller has read to choose the layout
// and to take the entry of the root: its layout version, the bytes of its root payload, and the bytes of
// each value stored out of line, in the order of the descriptors, and nothing else.
//
// Everything is checked before it is used, and before anything is allocated for the root:
// - the layout version against the layout;
// - the null bitmap: no bit set beyond the nullable fields, and the bit of each nullable String or
//   Binary field against its descriptor: a field that is null has the descriptor of zeros, one that is
//   not has a descriptor of a value;
// - the kind of each descriptor against its length, as RootWriter places values: inline at most
//   RootLayout.InlineLimit bytes, after the fixed part; in a block of its own 1 to
//   RootLayout.SingleBlockLimit bytes; in chunks more than that and at most ValueBlocks.MostValueBytes;
//   no other kind. The location of a value out of line is 0 on the wire: nothing received is used as an
//   address;
// - the total: the bytes of the input are the fixed part, the values inline and the values out of line,
//   exactly, and the payload fits one block; each value inline lies inside the payload.
// Each failure is a LatticeException with ValidationFailed, and nothing is allocated. Then the root
// block is allocated, prepared, and filled with the payload, and each value out of line is stored
// through ValueBlocks, the store the write path uses, which allocates its blocks and writes every header
// and location itself; the root block is named as their parent. The caller publishes the root.
//
// A refusal of the arena for its budget returns the null address, and an OutOfMemoryException of the
// native allocator passes through; either way nothing of the root is left allocated. The caller decides
// what a refusal means (Engine.Loading.RootPlacement).
//
// The arena has one changing thread at a time: the caller holds the lock of the writes of the child.
internal sealed class BlockImport
{
    private readonly Arena _arena;

    private readonly LatticeSchema _schema;

    // The root payload as received, checked here before any of it reaches the arena: at most the
    // payload of one block. Reused for every root.
    private readonly byte[] _payload = new byte[Arena.MaxBlockSize - BlockHeader.Size];

    public BlockImport(Arena arena, LatticeSchema schema)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(schema);

        _arena = arena;
        _schema = schema;
    }

    // Places the root of the layout for the lifetime the handle names (see the class): the address of
    // its root block, prepared, with the input advanced to its end; the null address when the arena
    // refused it for its budget. ValidationFailed, with nothing left allocated, when a length, a count or
    // a kind is not valid; nothing is dereferenced before it is checked. A layout of an entity type of
    // another schema is a caller's mistake, ArgumentException.
    public ArenaAddress Read(ref SequenceReader<byte> input, RootLayout layout, RootHandle root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (!_schema.TryGetEntityType(layout.EntityType.Name, out var entityType) || !ReferenceEquals(entityType, layout.EntityType))
        {
            throw new ArgumentException($"The layout of {layout.EntityType.Name} is not one of the schema of the child.", nameof(layout));
        }

        var payloadLength = ReadPayload(ref input, layout);

        if (!_arena.TryAllocatePrepared(payloadLength, BlockKindEnum.Root, root, out var block))
        {
            return default;
        }

        var index = 0;

        try
        {
            _arena.Header(block).LayoutVersion = layout.LayoutVersion;

            var target = _arena.Payload(block);

            _payload.AsSpan(0, payloadLength).CopyTo(target);

            for (; index < layout.DescriptorCount; index++)
            {
                var offset = layout.DescriptorField(index).Offset;
                var place = target.Slice(offset - BlockHeader.Size, ValueBlocks.DescriptorSize);
                var length = ValueBlocks.Read(place) switch
                {
                    { Kind: DescriptorKindEnum.Block or DescriptorKindEnum.Chunked } received => (int)received.Length,
                    _ => -1,
                };

                if (length < 0)
                {
                    continue;
                }

                // The descriptor in place is the received one, whose location is 0; TryStore writes the
                // descriptor of the stored value over it, or leaves it as it was.
                var source = ValueSource.Received(input.UnreadSequence.Slice(0, length));

                if (!ValueBlocks.TryStore(_arena, root, layout.LayoutVersion, prepared: true, length, ref source, place))
                {
                    Abandon(block, layout, index);

                    return default;
                }

                ValueBlocks.Adopt(_arena, ValueBlocks.Read(place), block, offset);
                input.Advance(length);
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

            switch (descriptor.Kind)
            {
                case DescriptorKindEnum.Inline when descriptor.Length <= RootLayout.InlineLimit && descriptor.Location >= (ulong)layout.FixedSize:
                    inline += descriptor.Length;
                    break;

                case DescriptorKindEnum.Block when descriptor.Length is > 0 and <= RootLayout.SingleBlockLimit && descriptor.Location == 0:
                case DescriptorKindEnum.Chunked when descriptor.Length is > RootLayout.SingleBlockLimit and <= ValueBlocks.MostValueBytes && descriptor.Location == 0:
                    outOfLine += descriptor.Length;
                    break;

                default:
                    throw Invalid(layout, $"the field {field.Field.Name} has a descriptor of the kind {descriptor.Kind} with the length {descriptor.Length} and the location {descriptor.Location}, which does not describe a stored value.");
            }
        }

        var payloadLength = total - outOfLine;

        if (payloadLength != fixedPart + inline || payloadLength > payload.Length)
        {
            throw Invalid(layout, $"it has {total} bytes after its layout version, where its fixed part of {fixedPart} bytes and its values describe {fixedPart + inline + outOfLine}.");
        }

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            ref readonly var field = ref layout.DescriptorField(index);
            var descriptor = ValueBlocks.Read(payload[(field.Offset - BlockHeader.Size)..]);

            if (descriptor.Kind == DescriptorKindEnum.Inline && descriptor.Location + descriptor.Length > (ulong)(BlockHeader.Size + payloadLength))
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

    // Frees what Read allocated for a root it did not complete: the values stored before the descriptor
    // at the index, then the root block. The descriptors from the index on still hold what was received,
    // or the null and inline values, and name no block of this arena.
    private void Abandon(ArenaAddress block, RootLayout layout, int before)
    {
        var target = _arena.Payload(block);

        for (var index = 0; index < before; index++)
        {
            ValueBlocks.Free(_arena, ValueBlocks.Read(target[(layout.DescriptorField(index).Offset - BlockHeader.Size)..]));
        }

        _arena.Free(block);
    }
}
