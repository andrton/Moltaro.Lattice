namespace Moltaro.Lattice.Engine.Protection;

// The gates of one database (milestone contract, "Reader protection, publication and compaction";
// arena protocol, "Lock and lifetime order"): the protection of the roots, in 1,024 stripes, and the
// catalog gate. Each is a ReaderWriterLockSlim, as the milestone contract starts with. A gate is
// held only for short synchronous work in memory: never across I/O or an await, and never while
// waiting for anything else than another gate in the order below.
//
// What each gate protects:
// - A root stripe protects the roots whose slot in the table of root entries falls on it, slot
//   modulo StripeCount: one logical protection for a root and every block it owns (Q06.1). A read
//   holds it shared while it checks the root entry and copies the root; a write holds it
//   exclusively while it installs a new root block in the entry, changes the entry's state and its
//   last mutation, and frees the blocks the root no longer uses. Roots that share a stripe can
//   contend; that bounds the memory of the gates (1,024 of them for any number of roots).
// - The catalog gate protects the primary index of every entity type and the list of free slots of
//   the table of root entries: a lookup holds it shared while it finds a key and claims the entry
//   it maps to, and a write holds it exclusively while it changes an index, grows one, or takes or
//   releases a slot. A claim is taken and dropped under it (LookupClaims). For an entity type with
//   a String key the index compares a key with the text stored in the root, so the root block an
//   indexed entry names, RootEntry.Root, is changed under the catalog gate as well.
//
// The order. Where one thread holds more than one gate, it takes them in this order and releases
// them in the reverse order: the lock of the writes (WriteExecutor), then one root stripe, then
// the catalog gate. A thread holds at most one root stripe in S06. A read holds one gate at a time
// and never waits for a root while it holds the catalog gate: it claims under the catalog gate,
// releases it, and only then takes the root's stripe. The arena has no gate of its own in S06,
// because only the thread that executes a write changes it (Storage.Arena).
internal sealed class RootGates
{
    // The number of root stripes, as the milestone contract starts with.
    public const int StripeCount = 1024;

    private readonly ReaderWriterLockSlim[] _stripes;

    // The gates are made when the database opens. They are not disposed: a ReaderWriterLockSlim
    // holds no unmanaged resource until it is contended, and then only wait handles that the
    // collector releases, so disposing them at a close, while a late caller might still reach for
    // one, would only trade an ObjectDisposedException of the database for one of a gate.
    public RootGates()
    {
        _stripes = new ReaderWriterLockSlim[StripeCount];

        for (var stripe = 0; stripe < StripeCount; stripe++)
        {
            _stripes[stripe] = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        }

        Catalog = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
    }

    // The catalog gate.
    public ReaderWriterLockSlim Catalog { get; }

    // The stripe that protects the root in this slot of the table of root entries.
    public ReaderWriterLockSlim Root(uint slot) => _stripes[slot % StripeCount];
}
