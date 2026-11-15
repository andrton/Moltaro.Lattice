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
    // The caller frees a root only when no reader can reach its blocks any more (arena protocol,
    // "Reclamation and relocation").
    public static void Free(Arena arena, RootLayout layout, ArenaAddress root) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");
}
