using Moltaro.Lattice.Engine.Protection;

namespace Moltaro.Lattice.Engine.Indexes;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet): the growth of the hash index,
// whose old table is let go of once the new one is in use (SplitAllBuckets in
// libs/storage/Tsavorite/cs/src/core/Index/Tsavorite/Implementation/SplitIndex.cs), at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation (see
// THIRD-PARTY-NOTICES.md).
//
// The version of the index, its stamp (TableAndVersion, TableAt, Grow, below), is adapted from
// crossbeam (https://github.com/crossbeam-rs/crossbeam), the type SeqLock of
// crossbeam-utils/src/atomic/seq_lock.rs at commit a28f40fdddb70bea8e5faf0efad9af0be4b9dbea; the
// project offers the MIT licence or Apache-2.0, and this file takes it under MIT, Copyright (c) 2019
// The Crossbeam Project Developers (see THIRD-PARTY-NOTICES.md).
//
// The primary index of one entity type: from the hash of a key to the slot of the root entry of its
// live root (decision on index structures, the addition of 16 November 2026: a hash table of the
// engine's own after the hash index of Garnet/Tsavorite). It holds its current table (IndexTable),
// which a lookup reads while the one writer inserts, deletes or grows it.
//
// Growth. Before an insert is published, Reserve makes sure the table has room for its entry, so that
// the publication allocates nothing (milestone contract, "Index preparation and traversal"). The main
// table grows by the count alone (IndexTable, "The capacity policy"): when it is full, the writer
// builds a table with twice the main buckets from the words of the current one,
// while readers go on with the current one, which nobody changes meanwhile because only the writer
// changes a table; it publishes the new table by one store and retires the old one by the epoch of
// the reads (Protection.RetiredMemory), which lets go of it once the readers that could hold it have
// left. Until then both are counted: the new one in Bytes, the old one in RetiredMemory.IndexBytes,
// and while the new one is built in ReplacementBytes. Tsavorite grows in phases, with chunks of the
// table split by the threads that meet them, because many threads change its index; here the one
// writer builds the whole table within the write that needs the room, as the decision on index
// structures selected ("serialize index mutations during the copy; readers may continue using the
// old map").
//
// The version changes with every change of the entries, an insert, a delete or a growth, so that an
// enumeration sees that the index changed under it (brief of S06, "Enumeration until S08"). It is the
// stamp of the current table in the protocol of crossbeam's SeqLock, which Entities.EntityTable
// describes for the stamp of a root entry. As in the source, the mark is 1, Growing, and every stamp
// is even: the first is 0, and each change stores the stamp before it plus 2, which stays even when
// it wraps. A growth stores the mark, then the grown table, then the new stamp, so that a step of an
// enumeration that loads the stamp, then the table, then the stamp again, and finds no mark and the
// same stamp, holds the table of that stamp and applies its position to that table alone (review
// round 1 of S08, part 1: a step had applied a position of the old table to the new one). What
// differs from the source: an insert or a delete stores the new stamp with no mark before it, since
// it changes entries of the current table, which a step reads as the table changes them, and not
// which table is current, which is what the two loads of the stamp frame; one writer stores the mark,
// with no swap; each load is an acquire and each store a release, with no fence; and a step that finds
// the mark backs off and loads again, as a read by key does (EntityTable), instead of taking a lock.
//
// Who calls what. Find, TableAndVersion and TableAt from reads inside the protection of reads and from
// the writer; everything else only from the thread that holds the lock of the writes.
internal sealed class PrimaryIndex
{
    // The first table: two main buckets and a page of one overflow bucket.
    private const int FirstBucketBits = 1;

    // The version while a growth publishes its table, the mark of the stamp (see the class): no
    // stamp is odd.
    private const int Growing = 1;

    private readonly RetiredMemory _retired;

    private IndexTable _table = new(FirstBucketBits);

    // The version and the bytes of a table being built, on lines of their own (IndexChanges).
    private IndexChanges _changes;

    // What the next growth runs for the tests of the engine (EngineDatabase.HoldNextGrowth); null when
    // no test armed it, which is all a growth pays: one load of a field nothing else writes.
    private Action? _growthHold;

    public PrimaryIndex(RetiredMemory retired)
    {
        _retired = retired;
    }

    // The bytes of the current table. For the writer and the counters.
    public long Bytes => _table.Bytes;

    // The bytes of the table being built while the index grows, 0 otherwise. The counters are taken
    // between two writes, so they read 0 here; it is the figure the budget of S09 charges while a
    // growth runs.
    public long ReplacementBytes => _changes.ReplacementBytes;

    // The entries of the hash in the current table. The table is read with acquire semantics, which
    // pairs with the release of the store that published it (Grow).
    public IndexProbe Find(uint hash) => new(Volatile.Read(ref _table), hash);

    // For the first step of an enumeration: the current table and the stamp it is the table of. Every
    // load is an acquire, so the table is loaded after the first load of the stamp and before the
    // second, and a table that a growth published after the first load comes with the mark or a later
    // stamp at the second (Grow). A growth that publishes its table meanwhile makes the step load them
    // again; that waits for the few stores of the publication, or for a test that holds a growth there
    // (EngineDatabase.HoldNextGrowth).
    public IndexTable TableAndVersion(out int version)
    {
        var backoff = default(SpinWait);

        while (true)
        {
            var stamp = Volatile.Read(ref _changes.Version);

            if (stamp == Growing)
            {
                backoff.SpinOnce();

                continue;
            }

            var table = Volatile.Read(ref _table);

            if (Volatile.Read(ref _changes.Version) == stamp)
            {
                version = stamp;

                return table;
            }
        }
    }

    // For a later step of an enumeration: the current table when the index is still at the stamp the
    // enumeration started on, null when it changed since. The loads are those of TableAndVersion; the
    // stamp of the enumeration is never the mark, so a growth in progress is a change as any other.
    public IndexTable? TableAt(int version)
    {
        if (Volatile.Read(ref _changes.Version) != version)
        {
            return null;
        }

        var table = Volatile.Read(ref _table);

        return Volatile.Read(ref _changes.Version) == version ? table : null;
    }

    // Makes room for the entry of one more key of the hash before it is published: grows the main
    // table when it is full, unless it is the largest, and then makes room in the chain of the hash
    // (IndexTable.Reserve). An OutOfMemoryException leaves the current table in place.
    public void Reserve(uint hash)
    {
        if (_table.IsFull && _table.BucketBits < IndexTable.MostBucketBits)
        {
            Grow();
        }

        _table.Reserve(hash);
    }

    // Adds the entry of a key that the index does not hold, after Reserve; IndexTable.Add says which
    // store publishes it.
    public void Add(uint hash, uint slot)
    {
        _table.Add(hash, slot);
        AdvanceVersion();
    }

    // Removes the entry of the slot, which the index holds under the hash.
    public void Remove(uint hash, uint slot)
    {
        _table.Remove(hash, slot);
        AdvanceVersion();
    }

    // Arms the switch of the tests: the next growth runs hold once (EngineDatabase.HoldNextGrowth).
    public void HoldNextGrowth(Action hold) => Volatile.Write(ref _growthHold, hold);

    private void Grow()
    {
        var current = _table;

        // Retiring the old table after the publication allocates nothing.
        _retired.Reserve();

        var grown = new IndexTable(current.BucketBits + 1);

        _changes.ReplacementBytes = grown.Bytes;
        current.CopyInto(grown);
        _changes.ReplacementBytes = grown.Bytes;

        // The stores of the stamp (see the class), each with release semantics. The mark first; the
        // release of the table's store keeps the mark before the grown table, so a step that loads
        // the grown table loads the mark or a later stamp after it (TableAndVersion, TableAt).
        var stamp = _changes.Version;

        Volatile.Write(ref _changes.Version, Growing);

        // The release of this store publishes the new table with every entry written into it: a reader
        // that reads the new table (Find) sees them all. A reader that still has the old one finds
        // the same entries in it, unchanged.
        Volatile.Write(ref _table, grown);

        // The switch of the tests, between the store that publishes the grown table and the store of
        // the stamp that ends the growth; it changes nothing when no test armed it.
        if (_growthHold is not null)
        {
            Interlocked.Exchange(ref _growthHold, null)?.Invoke();
        }

        // The new stamp last; its release keeps the grown table before it.
        Volatile.Write(ref _changes.Version, stamp + 2);
        _changes.ReplacementBytes = 0;
        _retired.RetireTable(current, current.Bytes);
    }

    // The new stamp of an insert or a delete, with no mark before it (see the class).
    private void AdvanceVersion() => Volatile.Write(ref _changes.Version, _changes.Version + 2);
}
