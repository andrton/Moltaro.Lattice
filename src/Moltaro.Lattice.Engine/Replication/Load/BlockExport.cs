using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Writes one root of the arena as it travels in a load: the stored bytes travel and no field is decoded
// or encoded. A root travels as its type id, its last mutation sequence, its layout version, the bytes of
// its root payload, then the bytes of each value stored out of line in the order of its descriptors. The
// type id and the last mutation sequence belong to the entry of the root, so the capture writes them
// (Engine.Children); Write writes the rest, from the blocks of the root.
//
// Nothing on the wire is an address or a block number: the location of a value in a block of its own or
// in chunks is the address of that block in this arena, so the copy of the payload carries 0 in its
// place, and the child stores the value through its own allocation, which writes every location
// (BlockImport). The text of a field with Dictionary storage travels so too, as bytes with each root
// (docs/decisions/strings.md, "Q04.3: Where should identical strings share storage?"): its descriptor of
// kind Text with its length and the location 0, and the bytes of its entry among the values out of line,
// in the order of the descriptors; the child builds its own entries from them. No transfer id and no
// state of a connection. The bytes of a slot that hold no value, and the headers of the blocks, are not
// sent.
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

        foreach (var descriptor in new RootDescriptors(layout, payload))
        {
            if (ValueBlocks.LiesOutOfLine(descriptor))
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

        var copied = new RootDescriptors(layout, copy);

        while (copied.MoveNext())
        {
            var descriptor = copied.Current;

            if (ValueBlocks.LiesOutOfLine(descriptor))
            {
                ValueBlocks.Write(copied.Place, descriptor with { Location = 0 });
            }
        }

        output.Advance(payload.Length);

        foreach (var descriptor in new RootDescriptors(layout, payload))
        {
            if (!ValueBlocks.LiesOutOfLine(descriptor))
            {
                continue;
            }

            if (!ValueBlocks.LiesInChunks(descriptor))
            {
                output.Write(ValueBlocks.Contiguous(arena, descriptor, payload));

                continue;
            }

            var entries = ValueBlocks.Directory(arena, descriptor);

            for (var chunk = 0; chunk < entries.Length / ValueBlocks.DescriptorSize; chunk++)
            {
                output.Write(ValueBlocks.Chunk(arena, entries, chunk));
            }
        }
    }
}
