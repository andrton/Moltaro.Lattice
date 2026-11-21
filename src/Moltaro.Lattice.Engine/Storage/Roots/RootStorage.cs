using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Operations on one whole root in the arena, by the layout it was written with, and on one descriptor of a
// root. What a descriptor holds outside the root block is decided here by its kind, and only here: a Block
// or a Chunked descriptor names blocks the root owns (Values.ValueBlocks), a Text descriptor holds one
// reference to an entry of the text table (Text.TextTable), and an Inline or a Null descriptor holds
// nothing outside the block. An operation of a root walks its descriptors (RootDescriptors) and applies
// the operation of one descriptor to each.
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
    // The caller frees a root only when no reader can reach its blocks any more
    // (docs/design/arena-protocol.md, "Deferred reclamation"). A retired root is freed in the same
    // way: its blocks are still allocated and its descriptors as they were. A root with a Text
    // descriptor is freed by the thread that holds the lock of the writes, as every change of a count
    // is (Text.TextTable).
    public static void Free(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        foreach (var descriptor in new RootDescriptors(layout, layout.RootPayload(arena, root)))
        {
            FreeValue(arena, descriptor);
        }

        arena.Free(root);
    }

    // Publishes a root that a writer wrote, in prepared blocks: the root block and every block
    // it owns become live (Arena.Publish), when the write of the root is published, and so does the
    // entry of a Dictionary text that is still prepared because this root is the first published one
    // to refer to it (Text.TextTable.Publish). It changes no byte and allocates nothing. The root is
    // recognized as Free recognizes it, and refused in the same cases before anything is published.
    public static void Publish(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        foreach (var descriptor in new RootDescriptors(layout, layout.RootPayload(arena, root)))
        {
            PublishValue(arena, descriptor);
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

        var textBytes = 0L;

        foreach (var descriptor in new RootDescriptors(layout, layout.RootPayload(arena, root)))
        {
            textBytes += RetireValue(arena, descriptor);
        }

        arena.Retire(root);

        return textBytes;
    }

    // Frees what one descriptor of a block holds outside it: the blocks of a value the block owns, or the
    // one reference a Text descriptor holds to its entry, which the text table releases: a block holds
    // its references until it is freed (Text.TextTable). The one place where a reference goes: the
    // free of a root here, and of the values of a root that was not completed (RootWriter,
    // Replication.Load.BlockImport).
    public static void FreeValue(Arena arena, in ValueDescriptor descriptor)
    {
        switch (descriptor.Kind)
        {
            case DescriptorKindEnum.Text:
                arena.Texts.Release(ValueBlocks.AddressOf(descriptor));
                break;

            case DescriptorKindEnum.Block or DescriptorKindEnum.Chunked:
                ValueBlocks.Free(arena, descriptor);
                break;
        }
    }

    // Publishes what one descriptor of a root holds outside its block: the blocks of a value the root
    // owns, or the entry of a Text descriptor, which the text table publishes while it is still prepared.
    private static void PublishValue(Arena arena, in ValueDescriptor descriptor)
    {
        switch (descriptor.Kind)
        {
            case DescriptorKindEnum.Text:
                arena.Texts.Publish(ValueBlocks.AddressOf(descriptor));
                break;

            case DescriptorKindEnum.Block or DescriptorKindEnum.Chunked:
                ValueBlocks.Publish(arena, descriptor);
                break;
        }
    }

    // Retires what one descriptor of a published root holds outside its block: the blocks of a value the
    // root owns. The entry of a Text descriptor is not retired; the bytes of the slots it takes are
    // returned instead, for the count of Retire; 0 for every other descriptor.
    private static long RetireValue(Arena arena, in ValueDescriptor descriptor)
    {
        switch (descriptor.Kind)
        {
            case DescriptorKindEnum.Text:
                return ValueBlocks.SlotBytesOf((int)descriptor.Length);

            case DescriptorKindEnum.Block or DescriptorKindEnum.Chunked:
                ValueBlocks.Retire(arena, descriptor);
                break;
        }

        return 0;
    }

    // Names the root block as the parent of the blocks of the value one descriptor names, with the offset
    // of the descriptor in the root block. The blocks of a Text descriptor belong to their entry, which no
    // root block is the parent of.
    public static void AdoptValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress root, int descriptorOffset)
    {
        if (descriptor.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked)
        {
            ValueBlocks.Adopt(arena, descriptor, root, descriptorOffset);
        }
    }

    // The bytes of the slots of the blocks a root owns for the value one descriptor names: 0 for a Text
    // descriptor, whose entry belongs to the text table, and for an Inline or a Null one.
    public static long ValueSlotBytes(in ValueDescriptor descriptor) =>
        descriptor.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked ? ValueBlocks.SlotBytesOf((int)descriptor.Length) : 0;
}
