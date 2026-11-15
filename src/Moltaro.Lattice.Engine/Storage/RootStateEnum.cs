namespace Moltaro.Lattice.Storage;

// Where a root entry stands: whether its slot holds a lifetime, and how far that lifetime has
// come. Free is zero, so the entries of a new page are free as the page is allocated.
internal enum RootStateEnum : uint
{
    // The slot holds no lifetime. RootTable.Add can take it.
    Free = 0,

    // The root is published: a lookup reaches it.
    Live = 1,

    // The write that creates the root is prepared and not yet published.
    Prepared = 2,

    // The deletion of the root is published: a lookup that still holds a claim on the entry sees
    // absence. The slot is released only after the last claim has drained (arena protocol, "Lock
    // and lifetime order").
    Deleted = 3,

    // The generation of the slot is exhausted. The slot is never used again.
    Retired = 4,
}
