using System.Numerics;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Storage.Text;

// Heads are paged, so a new table is started without allocating its whole
// capacity. Writes owns both sets and their links, including page removal.
internal sealed class TextBucketSet(int capacity, uint tag, MemoryBudget? budget)
{
    private const int PageSize = 64;
    private const ulong Fibonacci = 0x9E3779B97F4A7C15;
    private readonly TextPages<Page> _pages = TextPages<Page>.Create(budget);
    private int _pageCount;

    public int Capacity { get; } = capacity;

    public uint Tag { get; } = tag;

    private int PageHeads => Math.Min(Capacity, PageSize);

    private static long OwnerBytes => MemoryBudget.ObjectBytes(2L * IntPtr.Size + 3 * sizeof(int));
    private long PageBytes => MemoryBudget.ObjectBytes(IntPtr.Size + 2L * sizeof(int)) + MemoryBudget.ArrayBytes<uint>(PageHeads);

    public static TextBucketSet Create(int capacity, uint tag, MemoryBudget? budget)
    {
        budget?.Reserve(OwnerBytes);
        try
        {
            var buckets = new TextBucketSet(capacity, tag, budget);
            budget?.Own(buckets, nameof(TextBucketSet));
            return buckets;
        }
        catch
        {
            budget?.Release(OwnerBytes);
            throw;
        }
    }

    public void ReleaseBacking()
    {
        System.Diagnostics.Debug.Assert(_pageCount == 0);
        _pages.ReleaseBacking();
        budget?.Release(OwnerBytes, this);
    }

    public void ReleaseAll()
    {
        while (_pages.TryNext(0, out var number, out var page))
        {
            DropPage(number, page);
        }

        ReleaseBacking();
    }

    public void ResetHeads()
    {
        for (var next = 0u; _pages.TryNext(next, out var number, out var page); next = number + 1)
        {
            Array.Clear(page.Heads);
            page.Occupied = 0;
            page.ParkedEntries = 0;
        }
    }

    public bool FullyBacked => _pageCount == Math.Max(1, Capacity / PageSize);

    public bool HasRoomFor(uint hash) => _pages.Get((uint)Bucket(hash) >> 6) is not null;

    // Parked heads are stale. Count the entries that could return to each
    // paid page so a withdrawal can drop any page whose last owner leaves.
    public void KeepParkedEntry(uint hash) => _pages.Get((uint)Bucket(hash) >> 6)!.ParkedEntries++;

    public void ReleaseParkedEntry(uint hash)
    {
        var number = (uint)Bucket(hash) >> 6;
        var page = _pages.Get(number)!;
        if (--page.ParkedEntries == 0)
        {
            DropPage(number, page);
        }
    }

    public void ReleaseParkedReservation(uint hash)
    {
        var number = (uint)Bucket(hash) >> 6;
        if (_pages.Get(number) is { ParkedEntries: 0 } page)
        {
            DropPage(number, page);
        }
    }

    public long Bytes => OwnerBytes + _pages.Bytes + PageBytes * _pageCount;

    public int Bucket(uint hash) => (int)((hash * Fibonacci) >> (64 - BitOperations.Log2((uint)Capacity)));

    public uint Head(int bucket) => _pages.Get((uint)bucket >> 6)?.Heads[bucket & 63] ?? 0;

    public void Reserve(int bucket, Action? beforePageAllocation = null)
    {
        var page = (uint)bucket >> 6;
        if (_pages.Get(page) is null)
        {
            beforePageAllocation?.Invoke();
            budget?.Reserve(PageBytes);
            Page? added = null;
            try
            {
                added = new Page(PageHeads);
                if (budget is not null)
                {
                    budget.Own(added, nameof(TextBucketSet));
                    budget.Own(added.Heads, nameof(TextBucketSet));
                }

                _pages.Set(page, added);
                _pageCount++;
            }
            catch
            {
                if (budget is not null)
                {
                    if (added is not null)
                    {
                        budget.Forget(added.Heads);
                        budget.Forget(added);
                    }

                    budget.Release(PageBytes);
                }

                throw;
            }
        }
    }

    public void ReleaseReservation(int bucket)
    {
        var number = (uint)bucket >> 6;
        if (_pages.Get(number) is { Occupied: 0 } page)
        {
            DropPage(number, page);
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
            DropPage(number, page);
        }
    }

    private void DropPage(uint number, Page page)
    {
        _pages.Set(number, null);
        _pageCount--;
        if (budget is not null)
        {
            budget.Forget(page.Heads);
            budget.Release(PageBytes, page);
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
        groups.Add(new TextAllocationShape(1, (int)(OwnerBytes - 2 * IntPtr.Size), 0, 0));
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
        public int ParkedEntries;
    }
}
