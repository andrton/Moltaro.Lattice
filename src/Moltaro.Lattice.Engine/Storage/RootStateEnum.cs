namespace Moltaro.Lattice.Storage;

// Where a root entry stands: whether its slot holds a lifetime, and how far that lifetime has
// come. Free is zero, so the entries of a new page are free as the page is allocated.
internal enum RootStateEnum : uint
{
    // The slot holds no lifetime. RootTable.Add can give it out.
    Free = 0,

    // The root is published: a lookup reaches it.
    Live = 1,

    // The entry was given out by RootTable.Add and belongs to a write that has not published the
    // root yet. Every new entry starts here.
    Prepared = 2,

    // The deletion of the root is published: a lookup that still holds a claim on the entry sees
    // absence. Its owner releases the slot only after the last claim has drained (arena protocol,
    // "Lock and lifetime order").
    Deleted = 3,

    // The slot was released at generation uint.MaxValue. It is never given out again.
    Retired = 4,
}
