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
// Growth. Before an insert is published, Reserve makes sure the table has room for its entry, so that
// the publication allocates nothing (milestone contract, "Index preparation and traversal"). When it
// has none, the writer builds a table with twice the main buckets from the words of the current one,
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
// Version changes with every change of the entries, an insert, a delete or a growth, so that an
// enumeration sees that the index changed under it (brief of S06, "Enumeration until S08").
//
// Who calls what. Find and TryFindNext from reads inside the protection of reads and from the writer;
// everything else only from the thread that holds the lock of the writes.
internal sealed class PrimaryIndex
{
    // The first table: two main buckets and one overflow bucket, 248 bytes.
    private const int FirstBucketBits = 1;

    private readonly RetiredMemory _retired;

    private IndexTable _table = new(FirstBucketBits);

    // The version and the bytes of a table being built, on lines of their own (IndexChanges).
    private IndexChanges _changes;

    public PrimaryIndex(RetiredMemory retired)
    {
        _retired = retired;
    }

    public int Version => Volatile.Read(ref _changes.Version);

    // The bytes of the current table. For the writer and the counters.
    public long Bytes => _table.Bytes;

    // The bytes of the table being built while the index grows, 0 otherwise. The counters are taken
    // between two writes, so they read 0 here; it is the figure the budget of S09 charges while a
    // growth runs.
    public long ReplacementBytes => _changes.ReplacementBytes;

    // The entries of the hash in the current table. The table is read with acquire semantics, which
    // pairs with the release of the store that published it (Grow).
    public IndexProbe Find(uint hash) => new(Volatile.Read(ref _table), hash);

    // The slot of the next entry from position on, in the current table; see IndexTable.TryFindNext.
    public bool TryFindNext(ref int position, out uint slot) => Volatile.Read(ref _table).TryFindNext(ref position, out slot);

    // Makes room for the entry of one more key of the hash before it is published: grows the table
    // when it has none. An OutOfMemoryException leaves the current table in place.
    public void Reserve(uint hash)
    {
        if (!_table.HasRoomFor(hash))
        {
            Grow();
        }
    }

    // Adds the entry of a key that the index does not hold, after Reserve; IndexTable.Add says which
    // store publishes it.
    public void Add(uint hash, uint slot)
    {
        _table.Add(hash, slot);
        Changed();
    }

    // Removes the entry of the slot, which the index holds under the hash.
    public void Remove(uint hash, uint slot)
    {
        _table.Remove(hash, slot);
        Changed();
    }

    private void Grow()
    {
        var current = _table;

        // Retiring the old table after the publication allocates nothing.
        _retired.Reserve();

        var grown = new IndexTable(current.BucketBits + 1);

        _changes.ReplacementBytes = grown.Bytes;
        current.CopyInto(grown);

        // The release of this store publishes the new table with every entry written into it: a reader
        // that reads the new table (Find) sees them all. A reader that still has the old one finds
        // the same entries in it, unchanged.
        Volatile.Write(ref _table, grown);
        _changes.ReplacementBytes = 0;
        _retired.RetireTable(current, current.Bytes);
        Changed();
    }

    private void Changed() => Volatile.Write(ref _changes.Version, _changes.Version + 1);
}
