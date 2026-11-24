using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Writes one root of the arena as it travels in a load: the stored bytes travel and no field is decoded
// or encoded. A root travels as its type id, its last mutation sequence, its layout version, the bytes of
// its root payload, then the values it holds out of line, in preorder: for each descriptor of the root, in
// their order, the bytes of the value stored out of line that it names; for a List of records, the payload
// of each block of its records, in their order, each followed by the values its records name, record by
// record and descriptor by descriptor, a List of records among them in the same form, at every depth. The
// type id and the last mutation sequence belong to the entry of the root, so the capture writes them
// (Engine.Children); Write writes the rest, from the blocks of the root.
//
// Nothing on the wire is an address or a block number: the location of a value in a block of its own, in
// chunks or in blocks of records is the address of that block in this arena, so the copy of a root payload
// and of a block of records carries 0 in its place, and the child stores the value through its own
// allocation, which writes every location (BlockImport). A value inline keeps its location, an offset in the
// block that holds it. Between two values nothing travels, no offset and no length but those the descriptors
// hold, so a value can only follow the one before it. The text of a field with Dictionary storage travels
// so too, as bytes with each root (docs/decisions/strings.md, "Q04.3: Where should identical strings share
// storage?"): its descriptor of kind Text with its length and the location 0, and the bytes of its entry
// among the values out of line, in their order; the child builds its own entries from them. No transfer id
// and no state of a connection. The bytes of a slot that hold no value, the headers of the blocks and the
// directories of chunks are not sent.
// The numbers of the payload and of the values are in the byte order of this machine, which the
// handshake announces (Protocol.LayoutFormat); the layout version is little-endian.
//
// The walk over what a root owns (StructureWalk) visits a List of records after its records, as a free
// needs; the wire carries the blocks of the records before the values of the records, so the export
// descends in that order itself, through the blocks of the List (RecordBlocks), and counts its length the
// same way, so that Length is what Write writes.
//
// The caller holds the protection of the root while Write reads it (Engine.Children.Capture): the
// blocks of a published root never change, and those of a root that a write replaced stay allocated
// until the reads that could reach them have left. A write of the master that is published while a child
// is registered writes the root it publishes, under the lock of the writes, which nothing else changes
// meanwhile (Engine.Delivery).
internal static class BlockExport
{
    // The bytes Write writes for the root: its layout version, its root payload and each value stored
    // out of line.
    public static long Length(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);
        var length = (long)sizeof(uint) + payload.Length;
        var descriptors = new RootDescriptors(layout, payload);

        while (descriptors.MoveNext())
        {
            var descriptor = descriptors.Current;

            if (ValueBlocks.LiesOutOfLine(descriptor))
            {
                length += descriptor.Length;
            }
            else if (descriptor.Kind == DescriptorKindEnum.Records)
            {
                length += RecordsLengthOfRoot(arena, layout, root, descriptors.Index);
            }
        }

        return length;
    }

    public static void Write(IBufferWriter<byte> output, Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);

        BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(sizeof(uint)), layout.LayoutVersion);
        output.Advance(sizeof(uint));

        // The payload fits one span: it is at most the payload of one block.
        var copy = output.GetSpan(payload.Length)[..payload.Length];

        payload.CopyTo(copy);

        var copied = new RootDescriptors(layout, copy);

        while (copied.MoveNext())
        {
            var descriptor = copied.Current;

            if (NamesBlocks(descriptor))
            {
                ValueBlocks.Write(copied.Place, descriptor with { Location = 0 });
            }
        }

        output.Advance(payload.Length);

        var descriptors = new RootDescriptors(layout, payload);

        while (descriptors.MoveNext())
        {
            var descriptor = descriptors.Current;

            if (ValueBlocks.LiesOutOfLine(descriptor))
            {
                WriteValue(output, arena, descriptor, payload);
            }
            else if (descriptor.Kind == DescriptorKindEnum.Records)
            {
                WriteRecordsOfRoot(output, arena, layout, root, descriptors.Index);
            }
        }
    }

    // The List of records of the descriptor at the index of the root, for Length and Write, called with what
    // a root of scalar fields holds in registers, so that the length and the copy of such a root, which has
    // no List of records, pay nothing in their frames for it. Inlined, the List of records grew the frame of
    // Length from 48 bytes to 64 and that of Write from 248 to 264 (JIT listing of a Release build).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long RecordsLengthOfRoot(Arena arena, RootLayout layout, ArenaAddress root, int index) =>
        RecordsLength(arena, DescriptorOfRoot(arena, layout, root, index), layout.Structure.DescriptorElement(index)!);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteRecordsOfRoot(IBufferWriter<byte> output, Arena arena, RootLayout layout, ArenaAddress root, int index) =>
        WriteRecords(output, arena, DescriptorOfRoot(arena, layout, root, index), layout.Structure.DescriptorElement(index)!);

    // The descriptor at the index of the root.
    private static ValueDescriptor DescriptorOfRoot(Arena arena, RootLayout layout, ArenaAddress root, int index) =>
        ValueBlocks.Read(layout.RootPayload(arena, root)[(layout.DescriptorField(index).Offset - BlockHeader.Size)..]);

    // Whether the descriptor names blocks of this arena, whose address the wire carries as 0: a value out of
    // line, or the records of a List that is not empty.
    private static bool NamesBlocks(in ValueDescriptor descriptor) =>
        ValueBlocks.LiesOutOfLine(descriptor) || (descriptor.Kind == DescriptorKindEnum.Records && descriptor.Location != 0);

    // The bytes WriteRecords writes for a List of records, its element the layout given.
    private static long RecordsLength(Arena arena, in ValueDescriptor records, StructureLayout element)
    {
        var length = 0L;
        var blocks = new RecordBlocks(arena, records, element);

        while (blocks.MoveNext())
        {
            var payload = arena.Payload(blocks.Block);

            length += payload.Length;

            for (var record = 0; record < blocks.Count; record++)
            {
                for (var index = 0; index < element.DescriptorCount; index++)
                {
                    var descriptor = RecordDescriptor(payload, element, record, index);

                    if (ValueBlocks.LiesOutOfLine(descriptor))
                    {
                        length += descriptor.Length;
                    }
                    else if (descriptor.Kind == DescriptorKindEnum.Records)
                    {
                        length += RecordsLength(arena, descriptor, element.DescriptorElement(index)!);
                    }
                }
            }
        }

        return length;
    }

    // A List of records (see the class): each block of its records, its payload with every location of a
    // block of this arena as 0, then the values its records name, in their order.
    private static void WriteRecords(IBufferWriter<byte> output, Arena arena, in ValueDescriptor records, StructureLayout element)
    {
        var blocks = new RecordBlocks(arena, records, element);

        while (blocks.MoveNext())
        {
            var payload = arena.Payload(blocks.Block);

            // A block of records fits one span: it is at most the payload of one block.
            var copy = output.GetSpan(payload.Length)[..payload.Length];

            payload.CopyTo(copy);

            for (var record = 0; record < blocks.Count; record++)
            {
                for (var index = 0; index < element.DescriptorCount; index++)
                {
                    var place = copy[(RecordOffset(element, record, index) - BlockHeader.Size)..];
                    var descriptor = ValueBlocks.Read(place);

                    if (NamesBlocks(descriptor))
                    {
                        ValueBlocks.Write(place, descriptor with { Location = 0 });
                    }
                }
            }

            output.Advance(payload.Length);

            for (var record = 0; record < blocks.Count; record++)
            {
                for (var index = 0; index < element.DescriptorCount; index++)
                {
                    var descriptor = RecordDescriptor(payload, element, record, index);

                    if (ValueBlocks.LiesOutOfLine(descriptor))
                    {
                        WriteValue(output, arena, descriptor, payload);
                    }
                    else if (descriptor.Kind == DescriptorKindEnum.Records)
                    {
                        WriteRecords(output, arena, descriptor, element.DescriptorElement(index)!);
                    }
                }
            }
        }
    }

    // The bytes of one value out of line, from its block or its chunks; the payload is that of the block that
    // holds its descriptor.
    private static void WriteValue(IBufferWriter<byte> output, Arena arena, in ValueDescriptor descriptor, ReadOnlySpan<byte> payload)
    {
        if (!ValueBlocks.LiesInChunks(descriptor))
        {
            output.Write(ValueBlocks.Contiguous(arena, descriptor, payload));

            return;
        }

        var entries = ValueBlocks.Directory(arena, descriptor);

        for (var chunk = 0; chunk < entries.Length / ValueBlocks.DescriptorSize; chunk++)
        {
            output.Write(ValueBlocks.Chunk(arena, entries, chunk));
        }
    }

    // The descriptor at the index of the record of a block of records whose payload is given.
    private static ValueDescriptor RecordDescriptor(ReadOnlySpan<byte> payload, StructureLayout element, int record, int index) =>
        ValueBlocks.Read(payload[(RecordOffset(element, record, index) - BlockHeader.Size)..]);

    // The offset in its block, the header counted, of the descriptor at the index of the record.
    private static int RecordOffset(StructureLayout element, int record, int index) =>
        BlockHeader.Size + (record * element.Size) + element.DescriptorField(index).Offset;
}
