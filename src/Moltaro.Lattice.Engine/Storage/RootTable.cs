namespace Moltaro.Lattice.Storage;

// The control entries of the roots of one database, in managed pages of EntriesPerPage entries
// (Q16, "Managed arrays of structs for control entries"; milestone contract, "Native layout and
// allocation"). The table grows by adding a page and never copies or moves an existing entry, so
// a reference to an entry stays a reference to that entry while pages are added.
//
// A slot is named by a RootHandle that carries the slot's generation. The first generation of a
// slot is 1, and every reuse advances it, so a handle of an earlier lifetime never reaches a later
// one. A slot released at generation uint.MaxValue is retired for good.
//
// In S04 the table takes no lock, and one thread at a time uses it. The claims and the protection
// of the arena protocol, which make lookup, deletion and reuse safe together, come with S06 to
// S08.
internal sealed class RootTable
{
    // The number of entries in one page.
    public const int EntriesPerPage = 4096;

    // Takes a free slot for a new lifetime of a root of the entity type typeId and returns its
    // handle, which carries the slot's next generation. When no slot is free, the table adds a
    // page; adding a page copies no existing entry. The entry gets the generation and typeId,
    // every other field zero, and the state Prepared: it belongs to a write that has not published
    // it yet.
    public RootHandle Add(uint typeId) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Whether the handle names the present lifetime of its slot: the slot still has the handle's
    // generation and is neither free nor retired, because Add gave the handle out and it has not
    // been released since. The default handle is never current.
    public bool IsCurrent(RootHandle handle) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // A reference to the entry of the lifetime the handle names, in its managed page. It stays a
    // reference to that entry while pages are added. ArgumentException when the handle is not
    // current.
    public ref RootEntry Entry(RootHandle handle) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Ends the lifetime the handle names, and the handle is no longer current. The entry becomes
    // Free, and Add can give the slot out again with its next generation; a slot released at
    // generation uint.MaxValue becomes Retired instead and is never given out again. Release looks
    // at neither ClaimCount nor State: who may release a lifetime, and when, is the caller's.
    // ArgumentException when the handle is not current.
    public void Release(RootHandle handle) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // How many slots hold a lifetime: given out by Add and not released since. A retired slot is
    // not counted.
    public int Count =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // How many pages the table has. Times EntriesPerPage, this is the number of its slots.
    public int Pages =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");
}
