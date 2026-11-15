namespace Moltaro.Lattice.Storage;

// Operations on one whole root in the arena, by the layout it was written with.
internal static class RootStorage
{
    // Frees a root written with the layout and every block it owns: the root block, and the blocks
    // of kind Value, ChunkDirectory and Chunk that its descriptors lead to. Text that a descriptor of
    // kind Text names belongs to its text entry, not to the root, and is not freed here (milestone
    // contract, "Collections and strings"). The entry of the root in the root table is not touched;
    // releasing it is a separate step (RootTable.Release).
    //
    // The caller frees a root only when no reader can reach its blocks any more (arena protocol,
    // "Reclamation and relocation").
    public static void Free(Arena arena, RootLayout layout, ArenaAddress root) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");
}
