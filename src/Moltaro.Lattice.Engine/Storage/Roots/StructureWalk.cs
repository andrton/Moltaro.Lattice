using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The one walk over everything a root owns: the descriptors of its structure and, for each List of kind
// Records, the descriptors of each of its records, in the block of records that holds them, recursively
// (IStructureVisitor). It descends only into a List whose elements hold descriptors, and no deeper than the
// schema allows a declaration to be, 32, so the recursion is bounded by the schema and not by the data. It
// reads the blocks of a root and changes none; the visitor may free, publish or retire what a descriptor
// names once the walk has passed it, since every descriptor beneath it was visited before it.
//
// A descriptor of kind Records whose location is 0 names no block, as an empty List does; a directory entry
// still zero names no chunk; a record whose writer stopped before it is zero and holds only descriptors of
// kind Null. A pending descriptor (ValueBlocks) has kind Null and location zero with the length or count
// of a value still to be placed. It names no storage, so the walk needs no special case for it. The walk
// serves a root its writer or its import left half written as well as a whole one.
//
// The caller holds what keeps the blocks of the root from being freed or changed while it walks: the lock of
// the writes, or the protection of reads for a published root.
internal static class StructureWalk
{
    // Walks the root at the address, written with the layout, calling the visitor for every descriptor it
    // owns at every depth, children first (IStructureVisitor). The root is recognized as RootReader recognizes
    // it, and refused with ArgumentException in the same cases, before anything is visited.
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

        WalkStructure(arena, layout.Structure, layout.RootPayload(arena, root), BlockHeader.Size, 0, root, ref visitor);
    }

    // Walks the root whose payload is given, for a visitor that needs no holder, which this walk gives as the
    // null address for a descriptor of the root: a reader that counts what a root holds (RootReader).
    public static void WalkPayload<TVisitor>(Arena arena, RootLayout layout, ReadOnlySpan<byte> payload, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor =>
        WalkStructure(arena, layout.Structure, payload, BlockHeader.Size, 0, default, ref visitor);

    // Walks the image of a root its writer collects, whose first bytes stand for the header of a root block
    // that does not exist yet, so the holder of its descriptors is the null address (RootWriter).
    public static void WalkImage<TVisitor>(Arena arena, RootLayout layout, Span<byte> image, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor =>
        WalkStructure(arena, layout.Structure, image, 0, 0, default, ref visitor);

    // Walks the value one descriptor names, the records of a List of kind Records with the layout of its
    // element, then visits the descriptor itself, as it lies at the offset of the block of the holder.
    public static void WalkValue<TVisitor>(Arena arena, in ValueDescriptor descriptor, StructureLayout? element, ArenaAddress holder, int offset, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
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
    private static void WalkStructure<TVisitor>(Arena arena, StructureLayout structure, ReadOnlySpan<byte> bytes, int origin, int at, ArenaAddress holder, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
        var count = structure.DescriptorCount;

        for (var index = 0; index < count; index++)
        {
            var offset = at + structure.DescriptorOffset + (index * ValueBlocks.DescriptorSize);
            var descriptor = ValueBlocks.Read(bytes[(offset - origin)..]);
            var element = descriptor.Kind == DescriptorKindEnum.Records ? structure.DescriptorElement(index) : null;

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
            WalkStructure(arena, element, payload, BlockHeader.Size, BlockHeader.Size + (record * element.Size), block, ref visitor);
        }
    }
}
