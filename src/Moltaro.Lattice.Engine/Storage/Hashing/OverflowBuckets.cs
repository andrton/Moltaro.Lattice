using System.Runtime.CompilerServices;

using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Storage.Hashing;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet): the allocator of the overflow
// buckets of its hash index, MallocFixedPageSize in
// libs/storage/Tsavorite/cs/src/core/Allocator/MallocFixedPageSize.cs, at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation (see
// THIRD-PARTY-NOTICES.md).
//
// The overflow buckets of one table of a primary index (IndexTable), apart from its main table, so that
// they grow by themselves: the main table grows by the number of entries it holds, and the entries
// of one hash that do not fit their bucket lengthen its chain of overflow buckets and nothing else.
// Keys of one hash cannot be told apart by a larger main table, so a main table that doubled until its
// overflow held every chain took 139 bytes an entry for composite keys whose hashes collided.
//
// What is taken from MallocFixedPageSize: buckets in pages of a fixed number, a page being a pinned
// array whose first record starts on a line; an address of a bucket that is its page and its place in
// the page; the address 0 kept for none; a counter of the buckets given out, from which the next one
// is taken, and a new page added when the pages are used up. What differs:
// - Pages are added by the writer before it publishes the writes that need them (Reserve, for as many
//   buckets as the inserts prepared and not published yet may take), never on the path of a read, and
//   Take refuses when Reserve has not made room: no path writes outside the pages. Tsavorite allocates
//   the next page ahead and takes addresses with an interlocked add, because many threads take them.
// - The number of buckets in a page is a sixteenth of the main buckets of the table, at least one, so
//   that the overflow of a small table is small and that of a large one grows by large steps.
//   Tsavorite's pages hold 2^16 records.
// - The directory of pages grows by a copy that holds the same pages, published by one store;
//   Tsavorite allocates its whole directory at once.
// - A bucket is not freed alone: an emptied overflow bucket stays linked to its chain, whose next
//   entries reuse it, and is let go of with its table when the table is replaced. Tsavorite has a
//   free list.
//
// Who reads and writes. Only the thread that holds the lock of the writes (Engine.Writes.WriteExecutor)
// takes buckets and adds pages. A reader inside the protection of reads reaches a bucket in one of two ways, and each goes
// through a load with acquire semantics of a store with release semantics that comes after the page,
// and after the directory that holds it, were stored (the .NET memory model, not that of x64):
// - by key, through a link it read (IndexTable.Add publishes the link after the bucket);
// - by an enumeration step, through Taken, the count of the buckets given out, which Take advances by
//   a store with release semantics after the page was added (Reserve comes before Take).
// The reader reads the directory after that load, by Read, and finds the page in it. The words of a
// bucket are read with acquire semantics as well, each pairing with the store that published it
// (IndexTable.Add); a word the reader sees as 0 is an empty word.
internal sealed class OverflowBuckets
{
    private const int LineBytes = 64;

    private const int WordsPerBucket = IndexTable.WordsPerBucket;

    // The pages in the order they were added, published by Volatile.Write when the directory is
    // replaced by a larger copy; only the first _pageCount are pages.
    private OverflowPage[] _pages;
    private readonly MemoryBudget? _budget;
    private SmallerDirectory? _smaller;

    private int _pageCount;
    private int _installedPages;

    // The buckets in a page, as a power of two.
    private readonly int _pageBits;

    // The buckets given out: their addresses are 1 to this.
    private long _taken;

    // Overflow buckets of pages of 2^pageBits buckets, with the first page allocated, so that a table
    // has one page of overflow buckets, a sixteenth of its main buckets and at least one, from the
    // moment it is made.
    public OverflowBuckets(int pageBits, MemoryBudget? budget = null)
    {
        _pageBits = pageBits;
        _budget = budget;
        _pages = new OverflowPage[1];
        var words = GC.AllocateArray<long>(WordsInPage(pageBits), pinned: true);
        _pages[0] = new OverflowPage(words, IndexTable.FirstOnLine(words));
        _pageCount = 1;
    }

    private static int WordsInPage(int bits) => (WordsPerBucket << bits) + LineBytes / sizeof(long) - 1;

    private static long OwnerBytes => MemoryBudget.ObjectBytes(3L * IntPtr.Size + sizeof(long) + 3 * sizeof(int));

    public static long FirstCapacity(int bits) => OwnerBytes + MemoryBudget.ArrayBytes<OverflowPage>(1) +
        MemoryBudget.ArrayBytes<long>(WordsInPage(bits));

    public void OwnFirst()
    {
        if (_budget is { } budget)
        {
            budget.Own(this, nameof(OverflowBuckets));
            budget.Own(_pages, nameof(OverflowBuckets));
            budget.Own(_pages[0].Words, nameof(OverflowBuckets));
        }
    }

    public void Published() => _installedPages = _pageCount;

    public void ReleaseBacking()
    {
        if (_budget is { } budget)
        {
            var pages = _pages;
            _pages = [];
            DropSmallerDirectories();
            for (var i = 0; i < _pageCount; i++)
            {
                var words = pages[i].Words;
                pages[i] = default;
                budget.Release(words);
            }

            budget.Release(pages);
            budget.Release(OwnerBytes, this);
        }
    }

    // The buckets given out, for a walk over all of them: the copy into a larger table and an
    // enumeration step. Read with acquire semantics.
    public long Taken => Volatile.Read(ref _taken);

    // The bytes of the pages, with the words that align them, and of the directory.
    public long Bytes => ((long)_pageCount * (((long)WordsPerBucket << _pageBits) + LineBytes / sizeof(long) - 1) * sizeof(long))
        + ((long)_pages.Length * Unsafe.SizeOf<OverflowPage>());

    // Whether a bucket can be taken without adding a page.
    public bool HasFree => _taken < ((long)_pageCount << _pageBits);

    public bool CanTake(int buckets) => ((long)_pageCount << _pageBits) - _taken >= buckets;

    // Makes sure that this many buckets can be taken without adding a page, by new pages when the
    // pages have fewer free. Before the publications that take them; an OutOfMemoryException leaves the
    // buckets as they were, with the pages added before it.
    public void Reserve(long buckets)
    {
        while (((long)_pageCount << _pageBits) - _taken < buckets)
        {
            AddPage();
        }
    }

    // A preparation may have grown the directory across several calls and let another write queue
    // behind it. Clear only the pages no current or future bucket can reach before returning them.
    public void ReleaseUnused(int buckets)
    {
        if (_budget is not { } budget || _installedPages == 0)
        {
            return;
        }

        var pageSize = 1L << _pageBits;
        var needed = Math.Max(_installedPages, (_taken + buckets + pageSize - 1) >> _pageBits);
        while (_pageCount > needed)
        {
            var index = --_pageCount;
            var words = _pages[index].Words;
            _pages[index] = default;
            for (var smaller = _smaller; smaller is not null; smaller = smaller.Previous)
            {
                if (index < smaller.Pages.Length)
                {
                    smaller.Pages[index] = default;
                }
            }

            budget.Release(words);
        }

        while (_smaller is { } smallerDirectory && _pageCount <= smallerDirectory.Pages.Length)
        {
            var discarded = _pages;
            Volatile.Write(ref _pages, smallerDirectory.Pages);
            _smaller = smallerDirectory.Previous;
            smallerDirectory.Previous = null;
            budget.Release(discarded);
            budget.Release(SmallerDirectory.OwnerBytes, smallerDirectory);
        }
    }

    public void KeepCapacity()
    {
        _installedPages = _pageCount;
        DropSmallerDirectories();
    }

    private void DropSmallerDirectories()
    {
        while (_smaller is { } smaller)
        {
            _smaller = smaller.Previous;
            smaller.Previous = null;
            _budget!.Release(smaller.Pages);
            _budget.Release(SmallerDirectory.OwnerBytes, smaller);
        }
    }

    // Takes the next bucket, whose words are all 0, and returns its address. For the writer, after
    // Reserve; InvalidOperationException when Reserve made no room, before anything is written.
    public long Take()
    {
        if (!HasFree)
        {
            throw new InvalidOperationException("No overflow bucket is free: the index was not reserved before the write.");
        }

        // The store with release semantics that makes the bucket walkable for an enumeration step
        // (Taken): the page that holds it, and the directory, were stored before it. Its words are
        // still 0; each entry written into it is published by its own store (IndexTable.Add).
        Volatile.Write(ref _taken, _taken + 1);

        return _taken;
    }

    // A word of the bucket at the address, with acquire semantics.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Read(long address, int word) => Volatile.Read(ref BucketAt(Volatile.Read(ref _pages), address)[word]);

    // The words of the bucket at the address, for the writer.
    public Span<long> BucketAt(long address) => BucketAt(_pages, address);

    private Span<long> BucketAt(OverflowPage[] pages, long address)
    {
        var index = address - 1;
        var page = pages[(int)(index >> _pageBits)];
        var bucket = (int)(index & ((1L << _pageBits) - 1));

        return page.Words.AsSpan(page.First + (bucket * WordsPerBucket), WordsPerBucket);
    }

    // Adds a page of empty buckets. The page and a larger directory are allocated before anything
    // changes. The directory is published before any bucket of the page is linked.
    private void AddPage()
    {
        var previous = _pages;
        var index = _pageCount;
        var grows = index == previous.Length;
        var length = grows ? previous.Length * 2 : previous.Length;
        var wordsLength = WordsInPage(_pageBits);
        var restore = grows && _installedPages != 0 && _budget is not null;
        var bytes = MemoryBudget.ArrayBytes<long>(wordsLength) + (grows ? MemoryBudget.ArrayBytes<OverflowPage>(length) : 0) +
            (restore ? SmallerDirectory.OwnerBytes : 0);
        _budget?.Reserve(bytes);

        var directory = previous;
        long[]? words = null;
        SmallerDirectory? smaller = null;
        try
        {
            words = GC.AllocateArray<long>(wordsLength, pinned: true);
            _budget?.Own(words, nameof(OverflowBuckets));
            if (grows)
            {
                directory = new OverflowPage[length];
                Array.Copy(previous, directory, index);
                _budget?.Own(directory, nameof(OverflowBuckets));
                if (restore)
                {
                    smaller = new SmallerDirectory(previous, _smaller);
                    _budget!.Own(smaller, nameof(OverflowBuckets));
                }
            }
        }
        catch
        {
            if (words is not null)
            {
                _budget?.Forget(words);
            }

            if (!ReferenceEquals(directory, previous))
            {
                _budget?.Forget(directory);
            }

            if (smaller is not null)
            {
                _budget!.Forget(smaller);
            }

            _budget?.Release(bytes);
            throw;
        }

        directory[index] = new OverflowPage(words, IndexTable.FirstOnLine(words));
        if (grows)
        {
            Volatile.Write(ref _pages, directory);
            if (smaller is not null)
            {
                _smaller = smaller;
            }
            else
            {
                _budget?.Release(previous);
            }
        }

        _pageCount++;
    }

    private sealed class SmallerDirectory(OverflowPage[] pages, SmallerDirectory? previous)
    {
        public static long OwnerBytes => MemoryBudget.ObjectBytes(2L * IntPtr.Size);
        public readonly OverflowPage[] Pages = pages;
        public SmallerDirectory? Previous = previous;
    }
}
