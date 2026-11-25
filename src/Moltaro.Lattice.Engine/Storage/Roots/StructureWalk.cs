using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Walks the logical graph of a root: its descriptors, the entries descriptor
// of each Lookup, and the descriptors of the records that Lists, Maps and Sets
// hold. The schema bounds recursion to its declaration depth. A visitor can
// decline an edge before descent, as the ownership walks do for aliases.
// The walk reads payloads. On the way out a visitor can publish, retire or
// free the blocks it owns because their descendants have been visited.
//
// A descriptor of kind Records whose location is 0 names no block, as an empty List does; a directory entry
// still zero names no chunk; a record whose writer stopped before it is zero and holds only descriptors of
// kind Null. A pending descriptor (ValueBlocks) has kind Null and location zero with the length or count
// of a value still to be placed. It names no storage, so the walk needs no special case for it. The walk
// serves a root its writer or its import left half written as well as a whole one.
//
// The caller holds what protects the native blocks it follows: the lock of the writes, or a
// published root's read epoch. A writer's canonical image starts the same walk into native blocks.
internal static class StructureWalk
{
    // Walks the root at the address using its layout. Each edge the visitor
    // enters is visited after its children. The root is validated as in RootReader.
    //
    // Not inlined. Inlined, the walk brings its loop and its locals into every caller of the operations of a
    // root (RootStorage), the publication of every write among them, and grows the frames their prologs clear
    // on every call. In the JIT listing of a Release build, inlined: ChangePublisher.Install 1,680 bytes with a
    // frame of 232; called: Install 175 bytes with a frame of 40, and Walk over Publishing 1,204 bytes with a
    // frame of 136, which Install calls once for the root it publishes.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Walk<TVisitor>(Arena arena, RootLayout layout, ArenaAddress root, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(layout);

        WalkStructure(arena, layout.Structure, layout.RootPayload(arena, root), BlockHeader.Size, 0, root, canonical: false, ref visitor);
    }

    // Walks the root whose payload is given, for a visitor that needs no holder, which this walk gives as the
    // null address for a descriptor of the root: a reader that counts what a root holds (RootReader).
    public static void WalkPayload<TVisitor>(Arena arena, RootLayout layout, ReadOnlySpan<byte> payload, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor =>
        WalkStructure(arena, layout.Structure, payload, BlockHeader.Size, 0, default, canonical: false, ref visitor);

    // Walks the image of a root its writer collects, whose first bytes stand for the header of a root block
    // that does not exist yet, so the holder of its descriptors is the null address (RootWriter).
    public static void WalkImage<TVisitor>(Arena arena, RootLayout layout, Span<byte> image, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor =>
        WalkStructure(arena, layout.Structure, image, 0, 0, default, canonical: true, ref visitor);

    // Walks the value one descriptor names, the records of a List of kind Records with the layout of its
    // element, then visits the descriptor itself, as it lies at the offset of the block of the holder.
    public static void WalkValue<TVisitor>(Arena arena, in ValueDescriptor descriptor, StructureLayout? element, ArenaAddress holder, int offset, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
        if (!visitor.EnterValue(arena, descriptor, holder, offset))
        {
            return;
        }

        if (descriptor.Kind == DescriptorKindEnum.Lookup)
        {
            var entries = CollectionLookup.Entries(arena, descriptor);
            WalkValue(arena, entries, element, ValueBlocks.AddressOf(descriptor), CollectionLookup.EntriesOffset, ref visitor);
            visitor.Visit(arena, descriptor, holder, offset, element);
            return;
        }

        if (descriptor.Kind != DescriptorKindEnum.Records)
        {
            visitor.Visit(arena, descriptor, holder, offset, null);

            return;
        }

        WalkRecords(arena, descriptor, element!, ref visitor);
        visitor.Visit(arena, descriptor, holder, offset, element);
    }

    // The descriptors of one structure, which lies at the offset at of the block whose bytes are given from
    // the offset origin of the block on. They close the structure one after another (StructureLayout), so a
    // descriptor is found by its index, and the layout of an element is looked up only for a List of kind
    // Records: a root without one reads no table of its layout here.
    private static void WalkStructure<TVisitor>(Arena arena, StructureLayout structure, ReadOnlySpan<byte> bytes, int origin, int at, ArenaAddress holder, bool canonical, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
        var count = structure.DescriptorCount;

        for (var index = 0; index < count; index++)
        {
            var offset = at + structure.DescriptorOffset + (index * ValueBlocks.DescriptorSize);
            var place = bytes[(offset - origin)..];
            var descriptor = canonical ? ValueBlocks.Read(place) : ResidentDescriptor.Read(place);
            var element = descriptor.Kind is DescriptorKindEnum.Records or DescriptorKindEnum.Lookup ? structure.DescriptorElement(index) : null;

            WalkValue(arena, descriptor, element, holder, offset, ref visitor);
        }
    }

    // The records of a List of kind Records: in one block, or in the chunks of its directory, every chunk but
    // the last holding RecordsPerChunk of them (RecordBlocks).
    private static void WalkRecords<TVisitor>(Arena arena, in ValueDescriptor descriptor, StructureLayout element, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
        var blocks = new RecordBlocks(arena, descriptor, element);

        while (blocks.MoveNext())
        {
            WalkBlock(arena, element, blocks.Block, blocks.Count, ref visitor);
        }
    }

    // The records of one block of records, count of them at the stride of the element.
    private static void WalkBlock<TVisitor>(Arena arena, StructureLayout element, ArenaAddress block, int count, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
        var payload = arena.Payload(block);

        visitor.EnterRecordBlock(payload, element, count);

        for (var record = 0; record < count; record++)
        {
            WalkStructure(arena, element, payload, BlockHeader.Size, BlockHeader.Size + (record * element.Size), block, canonical: false, ref visitor);
        }
    }
}
