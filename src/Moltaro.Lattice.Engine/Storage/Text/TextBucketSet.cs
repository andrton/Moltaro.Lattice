using System.Numerics;

namespace Moltaro.Lattice.Storage.Text;

// Heads are paged, so a new table is started without allocating its whole
// capacity. Writes owns both sets and their links, including page removal.
internal sealed class TextBucketSet(int capacity, uint tag)
{
    private const int PageSize = 64;
    private const ulong Fibonacci = 0x9E3779B97F4A7C15;
    private readonly TextPages<Page> _pages = new();
    private int _pageCount;

    public int Capacity { get; } = capacity;

    public uint Tag { get; } = tag;

    private int PageHeads => Math.Min(Capacity, PageSize);

    public long Bytes => 40 + _pages.Bytes + (56L + ((4L * PageHeads + 7) & ~7L)) * _pageCount;

    public int Bucket(uint hash) => (int)((hash * Fibonacci) >> (64 - BitOperations.Log2((uint)Capacity)));

    public uint Head(int bucket) => _pages.Get((uint)bucket >> 6)?.Heads[bucket & 63] ?? 0;

    public void Reserve(int bucket)
    {
        var page = (uint)bucket >> 6;
        if (_pages.Get(page) is null)
        {
            _pages.Set(page, new Page(PageHeads));
            _pageCount++;
        }
    }

    public void ReleaseReservation(int bucket)
    {
        var number = (uint)bucket >> 6;
        if (_pages.Get(number) is { Occupied: 0 })
        {
            _pages.Set(number, null);
            _pageCount--;
        }
    }

    public void SetHead(int bucket, uint head)
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
        if (page.Occupied == 0)
        {
            _pages.Set(number, null);
            _pageCount--;
        }
    }

    // Locates one page, without a scan across absent parts of the table.
    public bool TryPage(int from, out int first)
    {
        if (_pages.TryNext((uint)from >> 6, out var page, out _))
        {
            first = Math.Max(from, (int)(page << 6));
            return true;
        }

        first = 0;
        return false;
    }

    public void Measure(List<TextAllocationShape> groups)
    {
        groups.Add(new TextAllocationShape(1, 24, 0, 0));
        _pages.Measure(groups);
        if (_pageCount != 0)
        {
            groups.Add(new TextAllocationShape(_pageCount, 16, 0, 0));
            groups.Add(new TextAllocationShape(_pageCount, 0, 4, PageHeads));
        }
    }

    private sealed class Page(int heads)
    {
        public readonly uint[] Heads = new uint[heads];
        public int Occupied;
    }
}
