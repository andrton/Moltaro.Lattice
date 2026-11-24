using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Operations on a root's physical ownership (docs/design/arena-protocol.md).
// Block, Chunked, Records and Lookup descriptors name arena edges. A descriptor
// may reach a shared subtree whose current owner is another holder and offset.
// The ownership visitors distinguish those aliases before walking children.
// A Text descriptor holds one reference to a text table entry, while Inline
// and Null descriptors hold nothing outside their block.
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

    // Publishes the new prepared blocks and any prepared Dictionary text they
    // reference, then publishes the root block. A borrowed subtree is already
    // live. Its backlink moves to the new holder before the root is installed,
    // without descending into its children. Publication changes no payload
    // bytes and allocates nothing.
    public static void Publish(Arena arena, RootLayout layout, ArenaAddress root)
    {
        var publishing = new Publishing();

        StructureWalk.Walk(arena, layout, root, ref publishing);
        arena.Publish(root);
    }

    // Retires the old root and the blocks it still owns. An earlier reader can
    // still follow its full graph, including aliases of transferred subtrees.
    // A Dictionary text entry stays live while a physical Text descriptor
    // holds its reference. The retire walk leaves payloads intact and takes
    // no allocation. Free follows the same ownership rule after the readers drain.
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
    public static void FreeValue(Arena arena, in ValueDescriptor descriptor, StructureLayout? element, ArenaAddress holder, int offset)
    {
        var freeing = new Freeing();

        StructureWalk.WalkValue(arena, descriptor, element, holder, offset, ref freeing);
    }

    // Frees everything the descriptors of the image of a root that a writer did not complete name, at every
    // depth (RootWriter): the image stands for a root block that was never allocated.
    public static void FreeImage(Arena arena, RootLayout layout, Span<byte> image)
    {
        var freeing = new Freeing();

        StructureWalk.WalkImage(arena, layout, image, ref freeing);
    }

    // Moves an edge from the writer's image to the root block. A borrowed
    // value still belongs to a published holder, so it stays with that holder.
    public static void AdoptValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress root, int descriptorOffset)
    {
        if (OwnedAddress(descriptor) is { IsNull: false } block)
        {
            ref var header = ref arena.Header(block);
            if (header.Parent.IsNull && header.DescriptorOffset == (uint)descriptorOffset)
            {
                header.Parent = root;
            }
        }
    }

    // Text references belong to the table. All other out-of-line descriptor
    // forms with a location name a root-owned edge.
    public static ArenaAddress OwnedAddress(in ValueDescriptor descriptor) =>
        descriptor.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked or DescriptorKindEnum.Records or DescriptorKindEnum.Lookup
            ? ValueBlocks.AddressOf(descriptor)
            : default;

    private static bool Owns(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset)
    {
        var address = OwnedAddress(descriptor);
        if (address.IsNull)
        {
            return true;
        }

        ref readonly var header = ref arena.Header(address);
        return header.Parent == holder && header.DescriptorOffset == (uint)offset;
    }

    // Frees what each descriptor holds outside its block (FreeValue).
    private struct Freeing : IStructureVisitor
    {
        public readonly bool EnterValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset) =>
            Owns(arena, descriptor, holder, offset);

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
        public readonly bool EnterValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset)
        {
            var address = OwnedAddress(descriptor);
            if (address.IsNull)
            {
                return true;
            }

            ref var header = ref arena.Header(address);
            if (header.Parent == holder && header.DescriptorOffset == (uint)offset)
            {
                return true;
            }

            // The preparation checked the old edge. The writing lock and the
            // admission order keep the preceding version until this transfer.
            if (header.Root != arena.Header(holder).Root || arena.IsPrepared(address))
            {
                throw new InvalidOperationException("A published value cannot be transferred from this edge.");
            }

            header.Parent = holder;
            header.DescriptorOffset = (uint)offset;
            return false;
        }

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

        public readonly bool EnterValue(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset) =>
            Owns(arena, descriptor, holder, offset);

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
