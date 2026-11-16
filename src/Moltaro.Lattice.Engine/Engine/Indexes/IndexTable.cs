using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Engine.Indexes;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet): the hash bucket and its
// entries of libs/storage/Tsavorite/cs/src/core/Index/Tsavorite/HashBucket.cs and HashBucketEntry.cs,
// and the walk of FindTag in TsavoriteBase.cs, at commit 3d104a76ec410499545bc7fb39d0e3168f8ee709;
// MIT licence, Copyright (c) Microsoft Corporation (see THIRD-PARTY-NOTICES.md).
//
// One table of a primary index: buckets of one cache line, 64 bytes, each of eight words. The first
// seven words of a bucket are entries; the eighth links to an overflow bucket, or is 0. An entry is
// one machine word, read and written whole, so a reader sees it as it was before a change or after
// it, never half written:
//
//   bits 63..32: the hash of the key, 32 bits     bits 31..0: the slot of its root entry, plus one
//
// and 0 is an empty word. The index holds no key: a lookup compares the hash first and then the key
// the root stores, which the caller reads through the slot (Entities.EntityTable). The table holds
// no reference either, so the collector has nothing to trace in it.
//
// What is taken from Tsavorite: the bucket of 64 bytes with seven entries and a link; the entry of
// one word; a lookup that selects the bucket by the hash, compares a part of the hash stored in the
// entry, then the key where the record lives, and follows the link; the array allocated pinned and
// its first bucket aligned to the line, as Tsavorite allocates its table in managed memory. What
// differs, for one writer and the root entries of this engine:
// - The entry holds the slot of the root entry, not an address: the entry is the stable control
//   entry of a root (Q16), and an address changes when a root is replaced or moved.
// - It holds the whole 32-bit hash instead of a 15-bit tag beside a 48-bit address, so that a growth
//   places every entry from the word alone, without reading a root.
// - No tentative bit, no compare-and-swap and no latch: one writer changes the table, and it writes a
//   word whole with a store that publishes it. Several entries of one bucket may have the same hash;
//   Tsavorite keeps one entry for each tag and chains the records in its log, which roots do not do.
// - A delete clears its word. An overflow bucket stays linked until the table is replaced.
// - The overflow buckets are part of the table, allocated with it, so the bytes of a table do not
//   change until it is replaced.
// - The bucket is taken from the high bits of the hash multiplied by 2^64 divided by the golden ratio
//   (Fibonacci hashing), so that keys whose hashes differ only in high bits spread as well. Bucket b
//   of a table goes to buckets 2b and 2b + 1 of the table twice its size; Tsavorite splits bucket i
//   into i and i plus the old size.
//
// The capacity policy. A table of B main buckets holds at most 4B entries (MostEntriesPerBucket) and
// B / 16 overflow buckets, at least one. With hashes spread evenly, at most 4 entries per bucket on
// average leave about 5% of the buckets with an overflow, and an index takes 17 to 34 bytes an entry
// whatever its key type, against 28 to 56 for an Int64 key and 36 to 72 for a Guid or String key in
// the map of Arc.Collections before S08. A table whose overflow buckets run out before it holds 4B
// entries grows early, which uneven hashes can cause. The entries of a bucket of a table twice the size need no
// more overflow buckets than the bucket had, so the entries of a table always fit the next.
//
// Who reads and writes. Only the thread that holds the lock of the writes changes a table. A reader
// inside the protection of reads (Protection.ReadEpochs) reads it at the same time, by Read, which is
// a Volatile.Read: an acquire, which pairs with the release of the store that published the word or
// the link (Add), so a reader that sees an entry sees the root entry and the root it names, and a
// reader that sees a link sees the overflow bucket filled.
internal sealed class IndexTable
{
    // On average at most this many entries per main bucket; see the class.
    public const int MostEntriesPerBucket = 4;

    // The words of a bucket: seven entries, then the link.
    public const int WordsPerBucket = 8;

    private const int EntriesPerBucket = 7;

    private const int LinkWord = 7;

    // 2^64 divided by the golden ratio, odd.
    private const ulong Fibonacci = 0x9E3779B97F4A7C15;

    private const int LineBytes = 64;

    // The words of every bucket, main buckets first, then overflow buckets; pinned, so that the line
    // alignment of the first bucket holds for the life of the array.
    private readonly long[] _words;

    // The index of the first word of bucket 0, on a line boundary.
    private readonly int _first;

    // 64 less the bits of the number of main buckets.
    private readonly int _shift;

    private readonly int _mainBuckets;

    private readonly int _buckets;

    // The next overflow bucket that no bucket links to yet.
    private int _nextOverflow;

    // A table of 2^bucketBits main buckets, empty. bucketBits is at least 1. The array is allocated
    // before anything else, so an OutOfMemoryException leaves nothing behind.
    public IndexTable(int bucketBits)
    {
        Debug.Assert(bucketBits is >= 1 and < 31, "The main buckets are between 2 and 2^30.");

        _mainBuckets = 1 << bucketBits;
        _buckets = _mainBuckets + Math.Max(1, _mainBuckets / 16);
        _words = GC.AllocateArray<long>(_buckets * WordsPerBucket + (LineBytes / sizeof(long)) - 1, pinned: true);
        _first = FirstOnLine(_words);
        _shift = 64 - bucketBits;
        _nextOverflow = _mainBuckets;
    }

    public int BucketBits => 64 - _shift;

    // The entries the table holds. For the writer.
    public int Count { get; private set; }

    // The bytes of the table: its array, with the words that align it.
    public long Bytes => (long)_words.Length * sizeof(long);

    // The word of an entry.
    public static long EntryOf(uint hash, uint slot) => (long)(((ulong)hash << 32) | (slot + 1));

    // The main bucket of a hash.
    public int BucketOf(uint hash) => (int)((hash * Fibonacci) >> _shift);

    // A word of a bucket, with acquire semantics (see the class).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Read(int bucket, int word) => Volatile.Read(ref _words[_first + bucket * WordsPerBucket + word]);

    // Whether Add can add an entry of the hash without growing: the table holds fewer entries than its
    // policy allows, and the bucket of the hash or its overflow buckets have an empty word, or an
    // overflow bucket is left to link. For the writer.
    public bool HasRoomFor(uint hash)
    {
        if (Count >= MostEntriesPerBucket * _mainBuckets)
        {
            return false;
        }

        return _nextOverflow < _buckets || EmptyWordOf(hash, out _, out _);
    }

    // Adds the entry of a key that the index does not hold, after HasRoomFor. The entry is written by
    // one store with release semantics, into an empty word of the bucket of the hash or of one of its
    // overflow buckets: a reader that sees it sees what was written before it, the root entry and the
    // root among them. When those words are all taken, the entry is written into a new overflow bucket
    // first, which no reader reaches yet, and the link to that bucket is the store that publishes it.
    public void Add(uint hash, uint slot)
    {
        var entry = EntryOf(hash, slot);

        if (EmptyWordOf(hash, out var bucket, out var word))
        {
            Volatile.Write(ref WordAt(bucket, word), entry);
        }
        else
        {
            Debug.Assert(_nextOverflow < _buckets, "HasRoomFor left an overflow bucket.");

            var overflow = _nextOverflow++;

            WordAt(overflow, 0) = entry;
            Volatile.Write(ref WordAt(bucket, LinkWord), overflow);
        }

        Count++;
    }

    // Removes the entry of the slot, which the table holds under the hash, by one store of 0. A reader
    // that read the word before goes on with the slot it read, which the protection of reads keeps
    // from being given to another root while it is inside (Protection.RetiredMemory).
    public void Remove(uint hash, uint slot)
    {
        var entry = EntryOf(hash, slot);
        var bucket = BucketOf(hash);

        while (true)
        {
            for (var word = 0; word < EntriesPerBucket; word++)
            {
                if (WordAt(bucket, word) == entry)
                {
                    Volatile.Write(ref WordAt(bucket, word), 0);
                    Count--;

                    return;
                }
            }

            bucket = (int)WordAt(bucket, LinkWord);

            if (bucket == 0)
            {
                throw new UnreachableException("A write removes an entry only after it found it.");
            }
        }
    }

    // Copies every entry into a larger table that no reader reaches yet. For the writer.
    public void CopyInto(IndexTable grown)
    {
        for (var bucket = 0; bucket < _nextOverflow; bucket++)
        {
            for (var word = 0; word < EntriesPerBucket; word++)
            {
                var entry = WordAt(bucket, word);

                if (entry != 0)
                {
                    grown.Add((uint)((ulong)entry >> 32), (uint)entry - 1);
                }
            }
        }
    }

    // The slot of the next entry from position on, in the order of the words of the table, and the
    // position after it; false when there is none. A position counts the entry words of the buckets.
    public bool TryFindNext(ref int position, out uint slot)
    {
        while (position < _buckets * EntriesPerBucket)
        {
            var entry = Read(position / EntriesPerBucket, position % EntriesPerBucket);

            position++;

            if (entry != 0)
            {
                slot = (uint)entry - 1;

                return true;
            }
        }

        slot = 0;

        return false;
    }

    // The index of the first word on a line boundary. The array is pinned, so its address stays.
    private static unsafe int FirstOnLine(long[] words)
    {
        var address = (nint)Unsafe.AsPointer(ref words[0]);
        var misalignment = (int)(address & (LineBytes - 1));

        return misalignment == 0 ? 0 : (LineBytes - misalignment) / sizeof(long);
    }

    private ref long WordAt(int bucket, int word) => ref _words[_first + bucket * WordsPerBucket + word];

    // The first empty word of the bucket of the hash or of its overflow buckets; false, with the last
    // bucket of the chain, when there is none.
    private bool EmptyWordOf(uint hash, out int bucket, out int word)
    {
        bucket = BucketOf(hash);

        while (true)
        {
            for (word = 0; word < EntriesPerBucket; word++)
            {
                if (WordAt(bucket, word) == 0)
                {
                    return true;
                }
            }

            var link = (int)WordAt(bucket, LinkWord);

            if (link == 0)
            {
                word = 0;

                return false;
            }

            bucket = link;
        }
    }
}
