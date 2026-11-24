using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Operations on one whole root in the arena, by the layout it was written with, and on one descriptor of a
// root. What a descriptor holds outside the block that holds it is decided here by its kind, and only here:
// a Block or a Chunked descriptor names blocks the root owns (Values.ValueBlocks), a Records descriptor names
// the block or the directory of the records of a List, which the root owns as well, a Text descriptor holds
// one reference to an entry of the text table (Text.TextTable), and an Inline or a Null descriptor holds
// nothing outside the block. An operation of a root walks every descriptor it owns at every depth, those of
// the records of its Lists included, children first (StructureWalk), and applies the operation of one
// descriptor to each.
internal static class RootStorage
{
    // Frees a root written with the layout: the root block and every block it owns, the value
    // blocks, chunk directories, chunks and blocks of records that RootLayout places, and the reference each
    // Text descriptor holds to the entry of its Dictionary text, which belongs to the text table and not to
    // the root (FreeValue). It does not touch the table of root entries; releasing the entry of the root
    // is a separate step (RootTable.Release).
    //
    // The root is recognized as RootReader recognizes it, and refused with ArgumentException in the
    // same cases, before anything is freed. Its descriptors are the only way to what it owns, so
    // every owned block is freed first, the blocks a block of records names before that block, and the root
    // block, which holds them, last.
    //
    // A root that a node did not finish placing from what it received is freed so too: what it did not place
    // is pending and names nothing (ValueBlocks.WritePending).
    //
    // The caller frees a root only when no reader can reach its blocks any more
    // (docs/design/arena-protocol.md, "Deferred reclamation"). A retired root is freed in the same
    // way: its blocks are still allocated and its descriptors as they were. A root with a Text
    // descriptor is freed by the thread that holds the lock of the writes, as every change of a count
    // is (Text.TextTable).
    public static void Free(Arena arena, RootLayout layout, ArenaAddress root)
    {
        var freeing = new Freeing();

        StructureWalk.Walk(arena, layout, root, ref freeing);
        arena.Free(root);
    }

    // Publishes a root that a writer wrote, in prepared blocks: the root block and every block
    // it owns become live (Arena.Publish), when the write of the root is published, and so does the
    // entry of a Dictionary text that is still prepared because this root is the first published one
    // to refer to it (Text.TextTable.Publish). It changes no byte and allocates nothing. The root is
    // recognized as Free recognizes it, and refused in the same cases before anything is published.
    public static void Publish(Arena arena, RootLayout layout, ArenaAddress root)
    {
        var publishing = new Publishing();

        StructureWalk.Walk(arena, layout, root, ref publishing);
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
        var retiring = new Retiring();

        StructureWalk.Walk(arena, layout, root, ref retiring);
        arena.Retire(root);

        return retiring.TextBytes;
    }

    // Frees what one descriptor of a root holds outside the block that holds it: the blocks of a value the
    // root owns, or the one reference a Text descriptor holds to its entry, which the text table releases: a
    // block holds its references until it is freed (Text.TextTable). For a List of kind Records, whose element
    // has the layout given, everything its records own first, then its blocks. The one place where a
    // reference goes: the free of a root here, of the values of a root that was not completed (RootWriter),
    // and of a List whose placement did not complete (NestedValues).
    public static void FreeValue(Arena arena, in ValueDescriptor descriptor, StructureLayout? element)
    {
        var freeing = new Freeing();

        StructureWalk.WalkValue(arena, descriptor, element, default, 0, ref freeing);
    }

    // Frees everything the descriptors of the image of a root that a writer did not complete name, at every
    // depth (RootWriter): the image stands for a root block that was never allocated.
    public static void FreeImage(Arena arena, RootLayout layout, Span<byte> image)
    {
        var freeing = new Freeing();

        StructureWalk.WalkImage(arena, layout, image, ref freeing);
    }

    // Names the block that holds a descriptor as the parent of the blocks of the value the descriptor names,
    // with the offset of the descriptor in that block: the root block, when a writer completes the root. The
    // blocks of a Text descriptor belong to their entry, which no root block is the parent of; an empty List of
    // kind Records names no block.
    public static void AdoptValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress root, int descriptorOffset)
    {
        if (descriptor.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked or DescriptorKindEnum.Lookup || (descriptor.Kind == DescriptorKindEnum.Records && descriptor.Location != 0))
        {
            ValueBlocks.Adopt(arena, descriptor, root, descriptorOffset);
        }
    }

    // Frees what each descriptor holds outside its block (FreeValue).
    private struct Freeing : IStructureVisitor
    {
        public readonly void EnterRecordBlock(ReadOnlySpan<byte> payload, StructureLayout element, int count) { }

        public readonly void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element)
        {
            switch (descriptor.Kind)
            {
                case DescriptorKindEnum.Text:
                    arena.Texts.Release(ValueBlocks.AddressOf(descriptor));
                    break;

                case DescriptorKindEnum.Block or DescriptorKindEnum.Chunked:
                    ValueBlocks.Free(arena, descriptor);
                    break;

                case DescriptorKindEnum.Records when descriptor.Location != 0:
                    ValueBlocks.FreeBlocks(arena, ValueBlocks.AddressOf(descriptor), NestedValues.LiesInChunks(descriptor, element!));
                    break;

                case DescriptorKindEnum.Lookup:
                    foreach (var block in new LookupBlocks(arena, descriptor))
                    {
                        arena.Free(block);
                    }

                    break;
            }
        }
    }

    // Publishes what each descriptor of a root holds outside its block: the blocks of a value or of records
    // the root owns, or the entry of a Text descriptor, which the text table publishes while it is still
    // prepared.
    private struct Publishing : IStructureVisitor
    {
        public readonly void EnterRecordBlock(ReadOnlySpan<byte> payload, StructureLayout element, int count) { }

        public readonly void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element)
        {
            switch (descriptor.Kind)
            {
                case DescriptorKindEnum.Text:
                    arena.Texts.Publish(ValueBlocks.AddressOf(descriptor));
                    break;

                case DescriptorKindEnum.Block or DescriptorKindEnum.Chunked:
                    ValueBlocks.Publish(arena, descriptor);
                    break;

                case DescriptorKindEnum.Records when descriptor.Location != 0:
                    ValueBlocks.PublishBlocks(arena, ValueBlocks.AddressOf(descriptor), NestedValues.LiesInChunks(descriptor, element!));
                    break;

                case DescriptorKindEnum.Lookup:
                    foreach (var block in new LookupBlocks(arena, descriptor))
                    {
                        arena.Publish(block);
                    }

                    break;
            }
        }
    }

    // Retires what each descriptor of a published root holds outside its block: the blocks of a value or of
    // records the root owns. The entry of a Text descriptor is not retired; the bytes of the slots it takes
    // are counted instead, for Retire.
    private struct Retiring : IStructureVisitor
    {
        public long TextBytes { get; private set; }

        public readonly void EnterRecordBlock(ReadOnlySpan<byte> payload, StructureLayout element, int count) { }

        public void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element)
        {
            switch (descriptor.Kind)
            {
                case DescriptorKindEnum.Text:
                    TextBytes += ValueBlocks.SlotBytesOf((int)descriptor.Length);
                    break;

                case DescriptorKindEnum.Block or DescriptorKindEnum.Chunked:
                    ValueBlocks.Retire(arena, descriptor);
                    break;

                case DescriptorKindEnum.Records when descriptor.Location != 0:
                    ValueBlocks.RetireBlocks(arena, ValueBlocks.AddressOf(descriptor), NestedValues.LiesInChunks(descriptor, element!));
                    break;

                case DescriptorKindEnum.Lookup:
                    foreach (var block in new LookupBlocks(arena, descriptor))
                    {
                        arena.Retire(block);
                    }

                    break;
            }
        }
    }
}
