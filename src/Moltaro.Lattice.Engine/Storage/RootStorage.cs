using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage;

// Operations on one whole root in the arena, by the layout it was written with.
internal static class RootStorage
{
    // Frees a root written with the layout: the root block and every block it owns, the value
    // blocks, chunk directories and chunks that RootLayout places. It does not touch the table of
    // root entries; releasing the entry of the root is a separate step (RootTable.Release). Text
    // that a descriptor of kind Text names belongs to its text entry, not to the root (milestone
    // contract, "Collections and strings").
    //
    // The root is recognized as RootReader recognizes it, and refused with ArgumentException in the
    // same cases, before anything is freed. Its descriptors are the only way to what it owns, so
    // every owned block is freed first and the root block, which holds them, last.
    //
    // The caller frees a root only when no reader can reach its blocks any more (arena protocol,
    // "Deferred reclamation, 16 November 2026"). A retired root is freed in the same way: its blocks
    // are still allocated and its descriptors as they were.
    public static void Free(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var offset = layout.DescriptorOffset + index * ValueBlocks.DescriptorSize - BlockHeader.Size;

            ValueBlocks.Free(arena, ValueBlocks.Read(payload[offset..]));
        }

        arena.Free(root);
    }

    // Publishes a root that a writer made with prepared true wrote: the root block and every block
    // it owns become live (Arena.Publish), when the write of the root is published. It changes no
    // byte and allocates nothing. The root is recognized as Free recognizes it, and refused in the
    // same cases before anything is published.
    public static void Publish(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var offset = layout.DescriptorOffset + index * ValueBlocks.DescriptorSize - BlockHeader.Size;

            ValueBlocks.Publish(arena, ValueBlocks.Read(payload[offset..]));
        }

        arena.Publish(root);
    }

    // The bytes of the slots of a root written with the layout: its root block and every block it owns,
    // each the whole slot of its size class (Arena.BlockSizeFor), as ArenaCounters.PreparedBytes counts
    // a prepared block. It reads the root block and changes nothing; the blocks a value owns are
    // counted from the length its descriptor gives, by the placement of RootLayout. The root is
    // recognized as Free recognizes it, and refused in the same cases.
    public static long SlotBytes(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);
        var bytes = (long)Arena.BlockSizeFor(payload.Length);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var offset = layout.DescriptorOffset + index * ValueBlocks.DescriptorSize - BlockHeader.Size;

            bytes += ValueBlocks.SlotBytes(ValueBlocks.Read(payload[offset..]));
        }

        return bytes;
    }

    // Retires a published root that a write replaced or deleted: the root block and every block it
    // owns become retired (Arena.Retire), so that the reads that reached it before go on reading one
    // whole state, and its bytes leave the live counters. It changes no byte and allocates nothing.
    // The root is recognized as Free recognizes it, and refused in the same cases before anything is
    // retired. Free frees it later, once no read can reach it.
    public static void Retire(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var offset = layout.DescriptorOffset + index * ValueBlocks.DescriptorSize - BlockHeader.Size;

            ValueBlocks.Retire(arena, ValueBlocks.Read(payload[offset..]));
        }

        arena.Retire(root);
    }
}
