namespace Moltaro.Lattice.Storage.Hashing;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet): the walk of a bucket and its
// overflow buckets in FindTag of libs/storage/Tsavorite/cs/src/core/Index/Tsavorite/TsavoriteBase.cs
// at commit 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft
// Corporation (see THIRD-PARTY-NOTICES.md).
//
// The entries of one hash in one table of a primary index, one after another: the slots of the root
// entries whose key has that hash, among them the one of the key looked for, if the table holds it.
// The caller compares each with the key the root stores (Engine.Entities.EntityTable). It reads the table as
// it is at each step, the main bucket of the hash first and then the overflow buckets its link leads
// to: a reader inside the protection of reads (Engine.Protection.ReadEpochs) may meet an entry the writer is
// adding, which it sees whole or not at all, and the slot of an entry the writer has removed, which
// the protection keeps from being given to another root while the reader is inside. A ref struct, so
// that it does not outlive the step of the read that made it.
internal ref struct IndexProbe
{
    private readonly IndexTable _table;

    private readonly uint _hash;

    // The main bucket of the hash while _overflow is 0; then the overflow bucket at that address.
    private readonly int _bucket;

    private long _overflow;

    private int _word;

    public IndexProbe(IndexTable table, uint hash)
    {
        _table = table;
        _hash = hash;
        _bucket = table.BucketOf(hash);
    }

    // The slot of the next entry with the hash; false when the bucket and its overflow buckets hold
    // no more.
    public bool Next(out uint slot)
    {
        while (true)
        {
            while (_word < IndexTable.EntriesPerBucket)
            {
                var entry = Read(_word++);

                if (entry != 0 && (uint)((ulong)entry >> 32) == _hash)
                {
                    slot = (uint)entry - 1;

                    return true;
                }
            }

            var link = Read(IndexTable.LinkWord);

            if (link == 0)
            {
                slot = 0;

                return false;
            }

            _overflow = link;
            _word = 0;
        }
    }

    private readonly long Read(int word) => _overflow == 0 ? _table.ReadMain(_bucket, word) : _table.ReadOverflow(_overflow, word);
}
