using Moltaro.Lattice.Engine.Protection;

namespace Moltaro.Lattice.Engine.Indexes;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet): the growth of the hash index,
// whose old table is let go of once the new one is in use (SplitAllBuckets in
// libs/storage/Tsavorite/cs/src/core/Index/Tsavorite/Implementation/SplitIndex.cs), at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation (see
// THIRD-PARTY-NOTICES.md).
//
// The primary index of one entity type: from the hash of a key to the slot of the root entry of its
// live root (decision on index structures, the addition of 16 November 2026: a hash table of the
// engine's own after the hash index of Garnet/Tsavorite). It holds its current table (IndexTable),
// which a lookup reads while the one writer inserts, deletes or grows it.
//
// Growth. Before an insert is published, Reserve makes sure the table has room for its entry, beside
// the entries of the other inserts prepared and not published yet, so that no publication allocates
// (milestone contract, "Index preparation and traversal"). The main table grows by the count, with
// those entries, alone (IndexTable, "The capacity policy"): when it is full, the writer
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
// The version of a table. Each table holds its version, which it is made with and which never changes:
// the first table has 0, and a growth makes the next table with the version after the current one's
// (IndexTable.Version). An enumeration keeps the version of the table its position belongs to, and a
// step that loads the current table finds its version in it, with no second load to frame, so the
// table and the version a step takes are always one pair (review round 1 of S08, part 1: a step had
// applied a position of the old table to the new one). An insert or a delete changes entries of the
// current table, which a step reads as they change, and not its version (Entities.EntityTable says
// why a walk stays correct through both).
//
// Who calls what. Find and TableAndVersion from reads, inside the protection of reads; Find also from a
// write being prepared, under the lock of the writes and outside the protection of reads, which is
// safe because only the holder of that lock changes the index and retires or frees what it held, so
// nothing the write reads changes or is freed while it holds the lock (Writes.WriteCoordinator);
// everything else only from the thread that holds the lock of the writes.
internal sealed class PrimaryIndex
{
    // The first table: two main buckets and a page of one overflow bucket.
    private const int FirstBucketBits = 1;

    private readonly RetiredMemory _retired;

    private IndexTable _table = new(FirstBucketBits, version: 0);

    // The bytes of the table being built while the index grows, 0 otherwise. Written by the writer
    // and read by the counters, both under the lock of the writes.
    private long _replacementBytes;

    // The inserts that have room reserved and are not published or withdrawn yet: while a group is in
    // the storage step, later writes are prepared and published after it, in their order
    // (Writes.WriteCoordinator). The writer's alone, under the lock of the writes.
    private int _reserved;

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
    public long ReplacementBytes => _replacementBytes;

    // The entries of the hash in the current table. The table is read with acquire semantics, which
    // pairs with the release of the store that published it (Grow).
    public IndexProbe Find(uint hash) => new(Volatile.Read(ref _table), hash);

    // For a step of an enumeration: the current table and its version (see the class). The table is
    // read with acquire semantics, as by Find, which pairs with the release of the store that published
    // it, so its version is the one it was made with.
    public IndexTable TableAndVersion(out int version)
    {
        var table = Volatile.Read(ref _table);

        version = table.Version;

        return table;
    }

    // Makes room for the entry of one more key of the hash before it is published, beside the inserts
    // that reserved room before and are not published or withdrawn yet: grows the main table when it is
    // full with them, unless it is the largest, and then makes room in the chain of the hash for this
    // one and for them (IndexTable.Reserve), in the table that is current then, so that a grown table
    // has the room of every reservation. Each reservation ends with Add or with Release. An
    // OutOfMemoryException leaves the current table in place and reserves nothing.
    public void Reserve(uint hash)
    {
        if (_table.IsFull(_reserved) && _table.BucketBits < IndexTable.MostBucketBits)
        {
            Grow();
        }

        _table.Reserve(hash, _reserved);
        _reserved++;
    }

    // Gives back the room an insert reserved and does not publish, because it was withdrawn.
    public void Release() => _reserved--;

    // Adds the entry of a key that the index does not hold, after Reserve, and ends that reservation;
    // IndexTable.Add says which store publishes it.
    public void Add(uint hash, uint slot)
    {
        _table.Add(hash, slot);
        _reserved--;
    }

    // Removes the entry of the slot, which the index holds under the hash.
    public void Remove(uint hash, uint slot) => _table.Remove(hash, slot);

    // Arms the switch of the tests: the next growth runs hold once (EngineDatabase.HoldNextGrowth).
    public void HoldNextGrowth(Action hold) => Volatile.Write(ref _growthHold, hold);

    private void Grow()
    {
        var current = _table;

        // Retiring the old table after the publication allocates nothing. An OutOfMemoryException
        // while the new table is built gives the place back and leaves the current table in place.
        _retired.Reserve();

        IndexTable grown;

        try
        {
            grown = new IndexTable(current.BucketBits + 1, current.Version + 1);
            _replacementBytes = grown.Bytes;
            current.CopyInto(grown);
            _replacementBytes = grown.Bytes;
        }
        catch
        {
            _replacementBytes = 0;
            _retired.Release();
            throw;
        }

        // The release of this store publishes the new table with its version and every entry written
        // into it: a reader that reads the new table (Find, TableAndVersion) sees them all. A reader
        // that still has the old one finds the same entries in it, unchanged.
        Volatile.Write(ref _table, grown);

        // The switch of the tests, after the store that publishes the grown table and before the old
        // one is retired; it changes nothing when no test armed it.
        if (_growthHold is not null)
        {
            Interlocked.Exchange(ref _growthHold, null)?.Invoke();
        }

        _replacementBytes = 0;
        _retired.RetireTable(current, current.Bytes);
    }
}
