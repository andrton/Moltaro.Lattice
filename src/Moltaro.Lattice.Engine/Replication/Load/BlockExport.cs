using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Writes one root of the arena as it travels in a load (brief of Epic 1, sprint S10; the planner's
// decision of 17 November 2026 after the study, point 2): the stored bytes travel and no field is
// decoded or encoded. A root travels as its type id, its last mutation sequence, its layout version, the
// bytes of its root payload, then the bytes of each value stored out of line in the order of its
// descriptors. The type id and the last mutation sequence belong to the entry of the root, so the
// capture writes them (Engine.Children); Write writes the rest, from the blocks of the root.
//
// Nothing on the wire is an address or a block number: the location of a value in a block of its own or
// in chunks is the address of that block in this arena, so the copy of the payload carries 0 in its
// place, and the child stores the value through its own allocation, which writes every location
// (BlockImport). The bytes of a slot that hold no value, and the headers of the blocks, are not sent.
// The numbers of the payload and of the values are in the byte order of this machine, which the
// handshake announces (Protocol.LayoutFormat); the layout version is little-endian.
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

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var descriptor = ValueBlocks.Read(payload[(layout.DescriptorField(index).Offset - BlockHeader.Size)..]);

            if (descriptor.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked)
            {
                length += (long)descriptor.Length;
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

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var place = copy[(layout.DescriptorField(index).Offset - BlockHeader.Size)..];
            var descriptor = ValueBlocks.Read(place);

            if (descriptor.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked)
            {
                ValueBlocks.Write(place, descriptor with { Location = 0 });
            }
        }

        output.Advance(payload.Length);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var descriptor = ValueBlocks.Read(payload[(layout.DescriptorField(index).Offset - BlockHeader.Size)..]);

            switch (descriptor.Kind)
            {
                case DescriptorKindEnum.Block:
                    Copy(output, ValueBlocks.Contiguous(arena, descriptor, payload));
                    break;

                case DescriptorKindEnum.Chunked:
                    var entries = ValueBlocks.Directory(arena, descriptor);

                    for (var chunk = 0; chunk < entries.Length / ValueBlocks.DescriptorSize; chunk++)
                    {
                        Copy(output, ValueBlocks.Chunk(arena, entries, chunk));
                    }

                    break;
            }
        }
    }

    // Copies the bytes into the output in as many spans as it gives.
    private static void Copy(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var place = output.GetSpan();
            var count = Math.Min(place.Length, bytes.Length);

            bytes[..count].CopyTo(place);
            output.Advance(count);
            bytes = bytes[count..];
        }
    }
}
