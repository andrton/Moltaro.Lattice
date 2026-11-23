namespace Moltaro.Lattice.Storage.Roots;

// The one walk over everything a root owns: the descriptors of its structure and, for each List of kind
// Records, the descriptors of each of its records, in the block of records that holds them, recursively
// (IStructureVisitor). It descends only into a List whose elements hold descriptors, and no deeper than the
// schema allows a declaration to be, 32, so the recursion is bounded by the schema and not by the data. It
// reads the blocks of a root and changes none; the visitor may free, publish or retire what a descriptor
// names once the walk has passed it.
//
// The caller holds what keeps the blocks of the root from being freed or changed while it walks: the lock of
// the writes, or the protection of reads for a published root.
internal static class StructureWalk
{
    // Walks the root at the address, written with the layout, calling the visitor for every descriptor it
    // owns at every depth, children first (IStructureVisitor). The root is recognized as RootReader recognizes
    // it, and refused with ArgumentException in the same cases, before anything is visited.
    public static void Walk<TVisitor>(Arena arena, RootLayout layout, ArenaAddress root, ref TVisitor visitor)
        where TVisitor : struct, IStructureVisitor
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(layout);

        throw new NotSupportedException($"The walk over the values of a root of {layout.EntityType.Name} at {root} for {typeof(TVisitor).Name} is not implemented yet.");
    }
}
