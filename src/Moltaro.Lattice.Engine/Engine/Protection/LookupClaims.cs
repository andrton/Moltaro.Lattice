using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Protection;

// The lifetimes of the roots of one database in its table of root entries, and the claims lookups
// hold on them (arena protocol, "Lock and lifetime order"; milestone contract, "Reader protection,
// publication and compaction"; brief of S06, the planner's answer to Q32).
//
// A lookup claims the entry an index maps its key to while it holds the catalog gate, so that the
// entry is neither released nor given to another root between the lookup and the read of the
// root; it drops the claim after it has released the root's stripe. The entry of a root whose
// delete is published is released when no claim is held on it: at once by the write when there is
// none, otherwise by the lookup that drops the last one. A released slot is given out again only by
// a later write, with the next generation, so an old claim never follows a recreated key into
// another lifetime.
//
// The list of free slots of the table changes under the catalog gate held exclusively, by a write
// that takes a slot and by a write or a lookup that releases one, which can happen at the same
// time. A claim is taken and dropped under the catalog gate as well: a delete removes the key from
// its index and looks at the claims in one exclusive section, so every claim is either counted
// there or taken by a lookup that no longer finds the key.
internal sealed class LookupClaims
{
    private readonly RootTable _table;

    private readonly RootGates _gates;

    public LookupClaims(RootTable table, RootGates gates)
    {
        _table = table;
        _gates = gates;
    }

    public RootTable Table => _table;

    // Takes a new lifetime for a root of the entity type, in the state Prepared. The write that took
    // it publishes it, or abandons it when it fails.
    public RootHandle Begin(uint typeId)
    {
        _gates.Catalog.EnterWriteLock();

        try
        {
            return _table.Add(typeId);
        }
        finally
        {
            _gates.Catalog.ExitWriteLock();
        }
    }

    // Releases a lifetime that was never published, of a write that failed after Begin. No index
    // ever mapped a key to it, so no claim is held on it.
    public void Abandon(RootHandle handle)
    {
        _gates.Catalog.EnterWriteLock();

        try
        {
            _table.Release(handle);
        }
        finally
        {
            _gates.Catalog.ExitWriteLock();
        }
    }

    // Claims a lifetime for a lookup. The caller holds the catalog gate and found the handle in an
    // index under it. Lookups claim at the same time under the shared gate, so the count changes
    // atomically.
    public void Claim(RootHandle handle) => Interlocked.Increment(ref _table.Entry(handle).ClaimCount);

    // Drops the claim of a lookup, after the lookup released the root's stripe. When it was the last
    // claim on a lifetime whose delete is published, the entry is released.
    public void Drop(RootHandle handle)
    {
        bool lastOfDeleted;

        _gates.Catalog.EnterReadLock();

        try
        {
            ref var entry = ref _table.Entry(handle);

            // No delete is published while the shared gate is held, so the state read here is the
            // state the claim was dropped in.
            lastOfDeleted = Interlocked.Decrement(ref entry.ClaimCount) == 0 && entry.State == RootStateEnum.Deleted;
        }
        finally
        {
            _gates.Catalog.ExitReadLock();
        }

        if (!lastOfDeleted)
        {
            return;
        }

        // The entry is still this lookup's to release: the delete was published before the claim was
        // dropped and found the claim held, no index maps a key to the entry any more, so no claim is
        // taken on it again, and only one lookup drops the count to zero.
        _gates.Catalog.EnterWriteLock();

        try
        {
            _table.Release(handle);
        }
        finally
        {
            _gates.Catalog.ExitWriteLock();
        }
    }

    // Ends the lifetime of a root whose delete is being published. The caller holds the catalog gate
    // exclusively, has removed the key from its index and set the entry to Deleted. The entry is
    // released at once when no lookup holds a claim on it; otherwise the last lookup releases it.
    public void EndDeleted(RootHandle handle)
    {
        if (_table.Entry(handle).ClaimCount == 0)
        {
            _table.Release(handle);
        }
    }
}
