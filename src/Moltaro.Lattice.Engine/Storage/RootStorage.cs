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
    // "Reclamation and relocation").
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
}
