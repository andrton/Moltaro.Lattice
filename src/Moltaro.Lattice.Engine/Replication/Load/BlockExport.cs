using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Writes one root to the wire and computes the exact length of what it writes. The capture adds
// the type id and last mutation sequence. This type writes the layout version, root payload and
// out-of-line values. The stored bytes are copied directly. Local addresses in the copied
// descriptors are zeroed, while inline offsets stay valid. A Map or Set sends its entries;
// the receiving node builds its own lookup.
//
// The root's descriptors are handled at the root level. For a List of records, the structure walk
// enters each block before visiting its descriptors, which gives the preorder wire format. The
// length calculation uses the same sequence of block and descriptor visits.
//
// The caller protects the root until the export ends. Capture holds read protection for a
// published root, and delivery holds the writes lock for a root it is publishing. See
// docs/design/arena-protocol.md, "Stored structures and root transfer," for the wire format.
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
            else if (descriptor.Kind is DescriptorKindEnum.Records or DescriptorKindEnum.Lookup)
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

            WriteDescriptor(arena, copied.Place, descriptor);
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
            else if (descriptor.Kind is DescriptorKindEnum.Records or DescriptorKindEnum.Lookup)
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
    private static long RecordsLengthOfRoot(Arena arena, RootLayout layout, ArenaAddress root, int index)
    {
        var visitor = new CountingRecords();

        WalkRecordsOfRoot(arena, layout, root, index, ref visitor);

        return visitor.Length;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WriteRecordsOfRoot(IBufferWriter<byte> output, Arena arena, RootLayout layout, ArenaAddress root, int index)
    {
        var visitor = new WritingRecords(output, arena);

        WalkRecordsOfRoot(arena, layout, root, index, ref visitor);
    }

    private static void WalkRecordsOfRoot<TVisitor>(Arena arena, RootLayout layout, ArenaAddress root, int index, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
        var offset = layout.DescriptorField(index).Offset;
        var descriptor = ValueBlocks.Read(layout.RootPayload(arena, root)[(offset - BlockHeader.Size)..]);

        StructureWalk.WalkValue(arena, descriptor, layout.Structure.DescriptorElement(index), root, offset, ref visitor);
    }

    private static void WriteDescriptor(Arena arena, Span<byte> place, ValueDescriptor descriptor)
    {
        if (descriptor.Kind == DescriptorKindEnum.Lookup)
        {
            descriptor = CollectionLookup.Entries(arena, descriptor);
        }
        else if (!ValueBlocks.LiesOutOfLine(descriptor) && descriptor.Kind != DescriptorKindEnum.Records)
        {
            return;
        }

        ValueBlocks.Write(place, descriptor with { Location = 0 });
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

    private struct CountingRecords : IStructureVisitor
    {
        public long Length { get; private set; }

        public readonly bool EnterValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset) => true;

        public void EnterRecordBlock(ReadOnlySpan<byte> payload, StructureLayout element, int count) =>
            Length += payload.Length;

        public void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element)
        {
            if (ValueBlocks.LiesOutOfLine(descriptor))
            {
                Length += descriptor.Length;
            }
        }
    }

    private readonly struct WritingRecords(IBufferWriter<byte> output, Arena arena) : IStructureVisitor
    {
        public bool EnterValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset) => true;

        public void EnterRecordBlock(ReadOnlySpan<byte> payload, StructureLayout element, int count)
        {
            // A block of records fits one span: it is at most the payload of one block.
            var copy = output.GetSpan(payload.Length)[..payload.Length];

            payload.CopyTo(copy);

            for (var record = 0; record < count; record++)
            {
                var descriptors = copy[((record * element.Size) + element.DescriptorOffset)..];

                for (var index = 0; index < element.DescriptorCount; index++)
                {
                    var place = descriptors[(index * ValueBlocks.DescriptorSize)..];
                    WriteDescriptor(arena, place, ValueBlocks.Read(place));
                }
            }

            output.Advance(payload.Length);
        }

        public void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element)
        {
            if (ValueBlocks.LiesOutOfLine(descriptor))
            {
                // This value is reached through its own location. It needs no bytes from the record block.
                WriteValue(output, arena, descriptor, payload: default);
            }
        }
    }
}
