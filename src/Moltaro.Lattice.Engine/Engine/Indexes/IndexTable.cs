using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Engine.Indexes;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet): the hash bucket and its
// entries of libs/storage/Tsavorite/cs/src/core/Index/Tsavorite/HashBucket.cs and HashBucketEntry.cs,
// and the walk of FindTag in TsavoriteBase.cs, at commit 3d104a76ec410499545bc7fb39d0e3168f8ee709;
// MIT licence, Copyright (c) Microsoft Corporation (see THIRD-PARTY-NOTICES.md).
//
// One table of a primary index: a main table of buckets of one cache line, 64 bytes, each of eight
// words, and the overflow buckets of the same form that its chains link to (OverflowBuckets). The
// first seven words of a bucket are entries; the eighth links to an overflow bucket by its address,
// or is 0. An entry is one machine word, read and written whole, so a reader sees it as it was before
// a change or after it, never half written:
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
// its first bucket aligned to the line, as Tsavorite allocates its table in managed memory; overflow
// buckets from an allocator of their own, apart from the main table. What differs, for one writer
// and the root entries of this engine:
// - The entry holds the slot of the root entry, not an address: the entry is the stable control
//   entry of a root (Q16), and an address changes when a root is replaced or moved.
// - It holds the whole 32-bit hash instead of a 15-bit tag beside a 48-bit address, so that a growth
//   places every entry from the word alone, without reading a root.
// - No tentative bit, no compare-and-swap and no latch: one writer changes the table, and it writes a
//   word whole with a store that publishes it. Several entries of one bucket may have the same hash;
//   Tsavorite keeps one entry for each tag and chains the records in its log, which roots do not do.
// - A delete clears its word, in the main table or in an overflow bucket. An overflow bucket stays
//   linked until the table is replaced.
// - The bucket is taken from the high bits of the hash multiplied by 2^64 divided by the golden ratio
//   (Fibonacci hashing), so that keys whose hashes differ only in high bits spread as well. Bucket b
//   of a table goes to buckets 2b and 2b + 1 of the table twice its size; Tsavorite splits bucket i
//   into i and i plus the old size.
//
// The capacity policy (review round 1 of S08, part 1). The main table of B buckets grows by the
// number of entries alone: once it holds 4B entries (MostEntriesPerBucket), the next insert makes a
// table of 2B. The chains grow by overflow alone: an entry that finds no empty word in its bucket and
// the overflow buckets linked to it takes a new overflow bucket, and the overflow adds a page when it
// has none left (OverflowBuckets). So keys of one hash, which no larger main table separates, cost
// their overflow and no more. With hashes spread evenly, at most 4 entries per bucket on average leave
// about 5% of the buckets with an overflow, and an index takes 17 to 34 bytes an entry whatever its
// key type, against 28 to 56 for an Int64 key and 36 to 72 for a Guid or String key in the map of
// Arc.Collections before S08.
//
// The largest main table has 2^27 buckets (MostBucketBits): its 2^30 words are the largest array of
// longs below Array.MaxLength, and 4 entries a bucket make 2^29 entries. Beyond, the main table no
// longer grows and the chains take the further entries through overflow, so the index refuses no
// insert of its own: the count of the root table of a database, below 2^31, bounds the entries first.
// Lookups get slower as the chains lengthen.
//
// Who reads and writes. Only the thread that holds the lock of the writes changes a table, and it
// calls Reserve before the publication of every insert, so that Add finds room without allocating;
// Add checks the room all the same and refuses before it writes. A reader inside the protection of
// reads (Protection.ReadEpochs) reads the table at the same time, and every way it can reach a word
// goes through a load with acquire semantics of a store with release semantics that comes after what
// it makes reachable (the .NET memory model, not that of x64):
// - the table itself: PrimaryIndex publishes a grown table by one store, after every word, page and
//   count of it was written, and a reader loads it with acquire semantics (PrimaryIndex.Find,
//   TableAndVersion, TableAt);
// - an entry: Add writes it by a store with release semantics after the insert wrote the root entry
//   and published the root (Writes.WriteExecutor), into the main table and into an overflow bucket
//   alike, and ReadMain and ReadOverflow load it with acquire semantics, so a reader that sees an
//   entry sees the root entry and the root it names;
// - an overflow bucket by key: the link to it, stored with release semantics after the bucket's first
//   entry, which a lookup loads with acquire semantics (IndexProbe);
// - an overflow bucket by an enumeration step: Taken, which OverflowBuckets.Take advances with release
//   semantics after the page that holds the bucket was added (TryFindNext);
// - a page of overflow buckets: the directory, published by a store with release semantics when it
//   grows, and otherwise a place of it that no reader reads before one of the two loads above
//   (OverflowBuckets.AddPage).
// The count of entries (Count, IsFull) is the writer's alone; no read loads it.
internal sealed class IndexTable
{
    // On average at most this many entries per main bucket; see the class.
    public const int MostEntriesPerBucket = 4;

    // The most main buckets, as a power of two; see the class.
    public const int MostBucketBits = 27;

    // The words of a bucket: seven entries, then the link.
    public const int WordsPerBucket = 8;

    public const int EntriesPerBucket = 7;

    public const int LinkWord = 7;

    // 2^64 divided by the golden ratio, odd.
    private const ulong Fibonacci = 0x9E3779B97F4A7C15;

    private const int LineBytes = 64;

    // The words of the main buckets; pinned, so that the line alignment of the first bucket holds for
    // the life of the array.
    private readonly long[] _words;

    // The index of the first word of bucket 0, on a line boundary.
    private readonly int _first;

    // 64 less the bits of the number of main buckets.
    private readonly int _shift;

    private readonly int _mainBuckets;

    private readonly OverflowBuckets _overflow;

    // The entries the table holds, on lines of their own (IndexTableCounts).
    private IndexTableCounts _counts;

    // A table of 2^bucketBits main buckets, empty, with overflow pages of a sixteenth of them, at
    // least one bucket. bucketBits is 1 to MostBucketBits. The arrays are allocated before anything
    // else, so an OutOfMemoryException leaves nothing behind.
    public IndexTable(int bucketBits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bucketBits, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bucketBits, MostBucketBits);

        _mainBuckets = 1 << bucketBits;
        _words = GC.AllocateArray<long>((_mainBuckets * WordsPerBucket) + (LineBytes / sizeof(long)) - 1, pinned: true);
        _first = FirstOnLine(_words);
        _shift = 64 - bucketBits;
        _overflow = new OverflowBuckets(Math.Max(0, bucketBits - 4));
    }

    public int BucketBits => 64 - _shift;

    // The entries the table holds. For the writer.
    public int Count => _counts.Entries;

    // Whether the main table holds as many entries as its policy allows, so that the next insert makes
    // a table of twice the main buckets, unless this one is the largest. For the writer.
    public bool IsFull => Count >= MostEntriesPerBucket * _mainBuckets;

    // The bytes of the table: its main array, with the words that align it, and its overflow.
    public long Bytes => ((long)_words.Length * sizeof(long)) + _overflow.Bytes;

    // The word of an entry.
    public static long EntryOf(uint hash, uint slot) => (long)(((ulong)hash << 32) | (slot + 1));

    // The index of the first word of a pinned array of longs that lies on a line boundary. The array
    // is pinned, so its address stays.
    public static unsafe int FirstOnLine(long[] words)
    {
        var address = (nint)Unsafe.AsPointer(ref words[0]);
        var misalignment = (int)(address & (LineBytes - 1));

        return misalignment == 0 ? 0 : (LineBytes - misalignment) / sizeof(long);
    }

    // The main bucket of a hash.
    public int BucketOf(uint hash) => (int)((hash * Fibonacci) >> _shift);

    // A word of a main bucket, with acquire semantics (see the class).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadMain(int bucket, int word) => Volatile.Read(ref _words[_first + (bucket * WordsPerBucket) + word]);

    // A word of an overflow bucket, by its address, with acquire semantics (see the class).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadOverflow(long address, int word) => _overflow.Read(address, word);

    // Makes room for an entry of the hash before the insert is published: an empty word in the bucket
    // of the hash or its overflow buckets, or an overflow bucket to take, adding a page to the overflow
    // when it has none left. For the writer; an OutOfMemoryException leaves the table as it was.
    public void Reserve(uint hash)
    {
        if (!EmptyWordOf(hash, out _, out _))
        {
            _overflow.Reserve();
        }
    }

    // Adds the entry of a key that the index does not hold, after Reserve. The entry is written by
    // one store with release semantics, into an empty word of the bucket of the hash or of one of its
    // overflow buckets: a reader that sees it sees what was written before it, the root entry and the
    // root among them. When those words are all taken, the entry is written into a new overflow bucket
    // first, which no reader reaches yet, and the link to that bucket is the store that publishes it.
    // InvalidOperationException, before anything is written, when Reserve did not make room.
    public void Add(uint hash, uint slot)
    {
        var entry = EntryOf(hash, slot);

        if (EmptyWordOf(hash, out var place, out var word))
        {
            Volatile.Write(ref BucketAt(place)[word], entry);
        }
        else
        {
            var overflow = _overflow.Take();

            // Published by its own store with release semantics, not by the link alone: an
            // enumeration step reaches the new bucket through Taken (TryFindNext), before or without
            // the link, and has to see the root entry and the root behind the entry it reads.
            Volatile.Write(ref _overflow.BucketAt(overflow)[0], entry);
            Volatile.Write(ref BucketAt(place)[LinkWord], overflow);
        }

        _counts.Entries++;
    }

    // Removes the entry of the slot, which the table holds under the hash, by one store of 0, in the
    // main table or in an overflow bucket. A reader that read the word before goes on with the slot it
    // read, which the protection of reads keeps from being given to another root while it is inside
    // (Protection.RetiredMemory).
    public void Remove(uint hash, uint slot)
    {
        var entry = EntryOf(hash, slot);
        long place = -BucketOf(hash);

        while (true)
        {
            var bucket = BucketAt(place);

            for (var word = 0; word < EntriesPerBucket; word++)
            {
                if (bucket[word] == entry)
                {
                    Volatile.Write(ref bucket[word], 0);
                    _counts.Entries--;

                    return;
                }
            }

            place = bucket[LinkWord];

            if (place == 0)
            {
                throw new InvalidOperationException("A write removes an entry only after it found it.");
            }
        }
    }

    // Copies every entry into a larger table that no reader reaches yet. For the writer.
    public void CopyInto(IndexTable grown)
    {
        for (var bucket = 0; bucket < _mainBuckets; bucket++)
        {
            CopyBucket(-bucket, grown);
        }

        for (var overflow = 1L; overflow <= _overflow.Taken; overflow++)
        {
            CopyBucket(overflow, grown);
        }
    }

    // The slot of the next entry from position on, in the order of the words of the table, and the
    // position after it; false when there is none. A position counts the entry words of the main
    // buckets, then those of the overflow buckets given out. It reaches an overflow bucket by its
    // address up to Taken, a load with acquire semantics that pairs with the store of Take, so the page
    // that holds the bucket is in the directory it then reads; every word is read with acquire
    // semantics, pairing with the store that published it (Add).
    public bool TryFindNext(ref long position, out uint slot)
    {
        var mainWords = (long)_mainBuckets * EntriesPerBucket;
        var end = mainWords + (_overflow.Taken * EntriesPerBucket);

        while (position < end)
        {
            var entry = position < mainWords
                ? ReadMain((int)(position / EntriesPerBucket), (int)(position % EntriesPerBucket))
                : ReadOverflow(((position - mainWords) / EntriesPerBucket) + 1, (int)((position - mainWords) % EntriesPerBucket));

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

    private void CopyBucket(long place, IndexTable grown)
    {
        var bucket = BucketAt(place);

        for (var word = 0; word < EntriesPerBucket; word++)
        {
            var entry = bucket[word];

            if (entry != 0)
            {
                grown.Place(entry);
            }
        }
    }

    // Writes an entry into this table while no reader reaches it yet, for CopyInto: into the first empty
    // word of its chain, or into a new overflow bucket, adding a page when the overflow has none left.
    // One walk of the chain, where Reserve and Add take two.
    private void Place(long entry)
    {
        if (EmptyWordOf((uint)((ulong)entry >> 32), out var place, out var word))
        {
            BucketAt(place)[word] = entry;
        }
        else
        {
            _overflow.Reserve();

            var overflow = _overflow.Take();

            _overflow.BucketAt(overflow)[0] = entry;
            BucketAt(place)[LinkWord] = overflow;
        }

        _counts.Entries++;
    }

    // The words of a bucket, for the writer: a place of 0 or below is the main bucket of that number
    // negated, a place above 0 the address of an overflow bucket.
    private Span<long> BucketAt(long place) =>
        place <= 0 ? _words.AsSpan(_first + ((int)-place * WordsPerBucket), WordsPerBucket) : _overflow.BucketAt(place);

    // The first empty word of the bucket of the hash or of its overflow buckets; false, with the last
    // bucket of the chain, when there is none.
    private bool EmptyWordOf(uint hash, out long place, out int word)
    {
        place = -BucketOf(hash);

        while (true)
        {
            var bucket = BucketAt(place);

            for (word = 0; word < EntriesPerBucket; word++)
            {
                if (bucket[word] == 0)
                {
                    return true;
                }
            }

            var link = bucket[LinkWord];

            if (link == 0)
            {
                word = 0;

                return false;
            }

            place = link;
        }
    }
}
