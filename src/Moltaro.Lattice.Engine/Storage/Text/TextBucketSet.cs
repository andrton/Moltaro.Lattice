using System.Numerics;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Storage.Text;

// Heads are paged, so a new table is started without allocating its whole
// capacity. Writes owns both sets and their links, including page removal.
// The heads of each page are charged to the budget before the page is made
// and released when it is dropped; a page emptied by a preparation that was
// refused or cancelled stays, for the next write (docs/design/storage-and-
// maintenance.md, the memory budget).
internal sealed class TextBucketSet(int capacity, uint tag, MemoryBudget budget)
{
    private const int PageSize = 64;
    private const ulong Fibonacci = 0x9E3779B97F4A7C15;
    private readonly MemoryBudget _budget = budget;
    private readonly TextPages<Page> _pages = new(budget);
    private int _pageCount;

    public int Capacity { get; } = capacity;

    public uint Tag { get; } = tag;

    // The fields above and those of Capacity and Tag: two references and three counts.
    private static readonly int FieldBytes = 2 * IntPtr.Size + 2 * sizeof(int) + sizeof(uint);

    private int PageHeads => Math.Min(Capacity, PageSize);

    public long Bytes => TextAllocationShape.ObjectBytes(FieldBytes) + _pages.Bytes + (TextAllocationShape.ObjectBytes(Page.FieldBytes) + PageBytes) * _pageCount;

    public int Bucket(uint hash) => (int)((hash * Fibonacci) >> (64 - BitOperations.Log2((uint)Capacity)));

    public uint Head(int bucket) => _pages.Get((uint)bucket >> 6)?.Heads[bucket & 63] ?? 0;

    // Whether the page of the bucket of the hash is there, so that a text of the hash takes no new page.
    public bool HasRoomFor(uint hash) => _pages.Get((uint)Bucket(hash) >> 6) is not null;

    private long PageBytes => MemoryBudget.ArrayBytes<uint>(PageHeads);

    public void Reserve(int bucket, Action? beforePageAllocation = null)
    {
        var page = (uint)bucket >> 6;
        if (_pages.Get(page) is null)
        {
            beforePageAllocation?.Invoke();
            var bytes = PageBytes;
            _budget.Reserve(bytes);
            try
            {
                _pages.Set(page, new Page(PageHeads));
            }
            catch
            {
                _budget.Release(bytes);
                throw;
            }

            _pageCount++;
        }
    }

    // A page that loses its last head is dropped, unless the change unwinds a preparation that was
    // refused or cancelled, whose capacity stays.
    public void SetHead(int bucket, uint head, bool keepsPage = false)
    {
        var number = (uint)bucket >> 6;
        var page = _pages.Get(number)!;
        ref var slot = ref page.Heads[bucket & 63];

        if (slot == 0 && head != 0)
        {
            page.Occupied++;
        }
        else if (slot != 0 && head == 0)
        {
            page.Occupied--;
        }

        slot = head;
        if (page.Occupied == 0 && !keepsPage)
        {
            _pages.Set(number, null);
            _pageCount--;
            _budget.Release(PageBytes);
        }
    }

    // Releases every page and branch, when the set is let go of after a migration.
    public void ReleaseAll()
    {
        _budget.Release(_pageCount * PageBytes);
        _pageCount = 0;
        _pages.ReleaseAll();
    }

    public void ReportHeld(List<HeldArray> held)
    {
        for (var page = 0; page < _pageCount; page++)
        {
            held.Add(new HeldArray(nameof(TextTable), typeof(uint), PageHeads));
        }

        _pages.ReportHeld(held);
    }

    // Locates one page, without a scan across absent parts of the table. A set of fewer than 64 buckets
    // has one page shorter than 64 heads: a bucket from past its last one is past every page, also when
    // the page stays empty for the next write.
    public bool TryPage(int from, out int first)
    {
        if (_pages.TryNext((uint)from >> 6, out var page, out _))
        {
            first = Math.Max(from, (int)(page << 6));
            return first < Capacity;
        }

        first = 0;
        return false;
    }

    public void Measure(List<TextAllocationShape> groups)
    {
        groups.Add(new TextAllocationShape(1, FieldBytes, 0, 0));
        _pages.Measure(groups);
        if (_pageCount != 0)
        {
            groups.Add(new TextAllocationShape(_pageCount, Page.FieldBytes, 0, 0));
            groups.Add(new TextAllocationShape(_pageCount, 0, sizeof(uint), PageHeads));
        }
    }

    private sealed class Page(int heads)
    {
        public readonly uint[] Heads = new uint[heads];
        public int Occupied;

        // The fields above: the reference to the heads and the count of the occupied ones.
        public static readonly int FieldBytes = IntPtr.Size + sizeof(int);
    }
}
