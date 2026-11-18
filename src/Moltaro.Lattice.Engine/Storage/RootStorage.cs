using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage;

// Operations on one whole root in the arena, by the layout it was written with.
internal static class RootStorage
{
    // Frees a root written with the layout: the root block and every block it owns, the value
    // blocks, chunk directories and chunks that RootLayout places, and the reference each Text
    // descriptor holds to the entry of its Dictionary text, which belongs to the text table and not to
    // the root (FreeValue). It does not touch the table of root entries; releasing the entry of the root
    // is a separate step (RootTable.Release).
    //
    // The root is recognized as RootReader recognizes it, and refused with ArgumentException in the
    // same cases, before anything is freed. Its descriptors are the only way to what it owns, so
    // every owned block is freed first and the root block, which holds them, last.
    //
    // The caller frees a root only when no reader can reach its blocks any more (arena protocol,
    // "Deferred reclamation, 16 November 2026"). A retired root is freed in the same way: its blocks
    // are still allocated and its descriptors as they were. A root with a Text descriptor is freed by
    // the thread that holds the lock of the writes, as every change of a count is (Text.TextTable).
    public static void Free(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var offset = layout.DescriptorOffset + index * ValueBlocks.DescriptorSize - BlockHeader.Size;

            FreeValue(arena, ValueBlocks.Read(payload[offset..]));
        }

        arena.Free(root);
    }

    // Frees what one descriptor of a block holds outside it: the blocks of a value the block owns, or the
    // one reference a Text descriptor holds to its entry, which the text table releases (the planner's
    // decision after the study of S13, point 4: a block holds its references until it is freed). The one
    // place where a reference goes: the free of a root here, and of the values of a root that was not
    // completed (RootWriter, Replication.Load.BlockImport).
    public static void FreeValue(Arena arena, in ValueDescriptor descriptor)
    {
        if (descriptor.Kind == DescriptorKindEnum.Text)
        {
            arena.Texts.Release(descriptor);
        }
        else
        {
            ValueBlocks.Free(arena, descriptor);
        }
    }

    // Publishes a root that a writer made with prepared true wrote: the root block and every block
    // it owns become live (Arena.Publish), when the write of the root is published, and so does the
    // entry of a Dictionary text that is still prepared because this root is the first published one
    // to refer to it (Text.TextTable.Publish). It changes no byte and allocates nothing. The root is
    // recognized as Free recognizes it, and refused in the same cases before anything is published.
    public static void Publish(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var offset = layout.DescriptorOffset + index * ValueBlocks.DescriptorSize - BlockHeader.Size;
            var descriptor = ValueBlocks.Read(payload[offset..]);

            if (descriptor.Kind == DescriptorKindEnum.Text)
            {
                arena.Texts.Publish(descriptor);
            }
            else
            {
                ValueBlocks.Publish(arena, descriptor);
            }
        }

        arena.Publish(root);
    }

    // Retires a published root that a write replaced or deleted: the root block and every block it
    // owns become retired (Arena.Retire), so that the reads that reached it before go on reading one
    // whole state, and its bytes leave the live counters. The entry of a Dictionary text is not retired:
    // it stays live, and the retired root keeps its reference until Free frees it. It changes no byte
    // and allocates nothing. The root is recognized as Free recognizes it, and refused in the same
    // cases before anything is retired. Free frees it later, once no read can reach it.
    //
    // Returns the bytes of the slots of the text entries that the Text descriptors of the root name:
    // each as a value of its length out of line takes them (ValueBlocks.SlotBytesOf), whether or not
    // other blocks share the entry. For the drain of retired memory, which counts them toward its next
    // look.
    public static long Retire(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payload = layout.RootPayload(arena, root);
        var textBytes = 0L;

        for (var index = 0; index < layout.DescriptorCount; index++)
        {
            var offset = layout.DescriptorOffset + index * ValueBlocks.DescriptorSize - BlockHeader.Size;
            var descriptor = ValueBlocks.Read(payload[offset..]);

            if (descriptor.Kind == DescriptorKindEnum.Text)
            {
                textBytes += ValueBlocks.SlotBytesOf((int)descriptor.Length);
            }

            ValueBlocks.Retire(arena, descriptor);
        }

        arena.Retire(root);

        return textBytes;
    }
}
