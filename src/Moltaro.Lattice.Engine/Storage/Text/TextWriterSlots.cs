using System.Numerics;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Storage.Text;

// Dense writer entries. Keep one empty trailing page available for
// the next write until bounded maintenance returns it. The extra cells
// keep mutable counts off the neighboring lines.
internal sealed class TextWriterSlots(MemoryBudget? budget)
{
    private const int Padding = 2;
    private const int PageSlots = 64 + 2 * Padding;
    private readonly TextPages<TextSlot[]> _pages = TextPages<TextSlot[]>.Create(budget);
    private int _pageCount;
    private int _installedPages;
    private int _installedFirst;
    private BackingHistory<TextSlot[]>? _earlierFirst;

    public int Count { get; private set; }

    private static long OwnerBytes => MemoryBudget.ObjectBytes(3L * IntPtr.Size + 4 * sizeof(int));

    public static TextWriterSlots Create(MemoryBudget? budget)
    {
        budget?.Reserve(OwnerBytes);
        try
        {
            var slots = new TextWriterSlots(budget);
            budget?.Own(slots, nameof(TextWriterSlots));
            return slots;
        }
        catch
        {
            budget?.Release(OwnerBytes);
            throw;
        }
    }

    public long Bytes
    {
        get
        {
            var bytes = OwnerBytes + _pages.Bytes + (_pageCount == 0 ? 0 :
                24L + 32L * _pages.Get(0)!.Length + (24L + 32L * PageSlots) * (_pageCount - 1));
            for (var earlier = _earlierFirst; earlier is not null; earlier = earlier.Earlier)
            {
                bytes += BackingHistory<TextSlot[]>.Bytes + MemoryBudget.ArrayBytes<TextSlot>(earlier.Backing.Length);
            }

            return bytes;
        }
    }

    public object? CapturePage(uint page) => _pages.Get(page);

    public ref TextSlot At(uint reference)
    {
        var index = reference - 1;
        return ref _pages.Get(index >> 6)![(index & 63) + Padding];
    }

    public void Reserve()
    {
        var page = (uint)Count >> 6;
        var previous = _pages.Get(page);
        var needed = page == 0 ? FirstCapacity(Count + 1) : PageSlots;
        if (previous is null)
        {
            var added = budget?.Allocate<TextSlot>(needed, nameof(TextWriterSlots)) ?? new TextSlot[needed];
            try
            {
                _pages.Set(page, added);
                _pageCount++;
            }
            catch
            {
                budget?.Release(added);
                throw;
            }
        }
        else if (previous.Length < needed)
        {
            ResizeFirst(previous, needed);
        }
    }

    public void ReleaseReservation()
    {
        if (Count == 0 && _pageCount != 0)
        {
            ReleaseLastPage();
        }
    }

    public uint Add(TextSlot slot)
    {
        var reference = (uint)++Count;
        At(reference) = slot;
        return reference;
    }

    public void Pop()
    {
        At((uint)Count) = default;
        Count--;
        if ((Count & 63) == 0)
        {
            // Crossing the next boundary can leave two empty pages. Keep
            // the nearer one, where the next new ordinal will arrive.
            if (_pageCount > (Count >> 6) + 1)
            {
                ReleaseLastPage();
            }

            ReleaseReservation();
        }
    }

    public bool TrimOne(bool releaseEmptyPage)
    {
        var usedPages = (int)(((uint)Count + 63) >> 6);
        if (releaseEmptyPage && _pageCount > usedPages)
        {
            ReleaseLastPage();
            return true;
        }

        if (Count is 0 or > 64)
        {
            return false;
        }

        var previous = _pages.Get(0)!;
        var needed = FirstCapacity(Count);
        if (previous.Length == needed)
        {
            return false;
        }

        ResizeFirst(previous, needed);
        _installedFirst = Math.Min(_installedFirst, needed);
        return true;
    }

    public void Keep(uint reference)
    {
        var page = (int)(reference - 1) >> 6;
        _installedPages = Math.Max(_installedPages, page + 1);
        _installedFirst = Math.Max(_installedFirst, FirstCapacity((int)Math.Min(reference, 64)));
        DropEarlierFirst(_installedFirst);
    }

    public void ReleaseUnused()
    {
        if (budget is null)
        {
            return;
        }

        var neededPages = Math.Max(_installedPages, (int)(((uint)Count + 63) >> 6));
        while (_pageCount > neededPages)
        {
            ReleaseLastPage();
        }

        if (_pageCount == 0 || Count > 64)
        {
            return;
        }

        var needed = Math.Max(_installedFirst, FirstCapacity(Count));
        while (_earlierFirst is { } earlier && earlier.Backing.Length >= needed)
        {
            var discarded = _pages.Get(0)!;
            discarded.AsSpan(0, earlier.Backing.Length).CopyTo(earlier.Backing);
            _pages.Set(0, earlier.Backing);
            _earlierFirst = earlier.Earlier;
            earlier.Drop(budget);
            budget.Release(discarded);
        }
    }

    private void DropEarlierFirst(int keeps)
    {
        ref var link = ref _earlierFirst;
        while (link is { } earlier)
        {
            if (earlier.Backing.Length >= keeps)
            {
                link = ref earlier.Earlier;
                continue;
            }

            link = earlier.Earlier;
            earlier.Drop(budget!);
            budget!.Release(earlier.Backing);
        }
    }

    private void ReleaseLastPage()
    {
        var number = (uint)--_pageCount;
        var previous = _pages.Get(number)!;
        _pages.Set(number, null);
        _installedPages = Math.Min(_installedPages, _pageCount);
        if (_pageCount == 0)
        {
            _installedFirst = 0;
            DropEarlierFirst(int.MaxValue);
        }

        budget?.Release(previous);
    }

    private void ResizeFirst(TextSlot[] previous, int needed)
    {
        var replacement = budget?.Allocate<TextSlot>(needed, nameof(TextWriterSlots)) ?? new TextSlot[needed];
        previous.AsSpan(0, Math.Min(previous.Length, needed)).CopyTo(replacement);
        if (budget is { IsPreparing: true })
        {
            try
            {
                _earlierFirst = BackingHistory<TextSlot[]>.Keep(previous, _earlierFirst, budget, nameof(TextWriterSlots));
            }
            catch
            {
                budget.Release(replacement);
                throw;
            }
        }
        else
        {
            DropEarlierFirst(int.MaxValue);
        }

        _pages.Set(0, replacement);
        if (budget is { IsPreparing: false })
        {
            budget.Release(previous);
        }
    }

    private static int FirstCapacity(int entries) => (int)BitOperations.RoundUpToPowerOf2((uint)entries) + 2 * Padding;

    public void Measure(List<TextAllocationShape> groups)
    {
        groups.Add(new TextAllocationShape(1, (int)(OwnerBytes - 2 * IntPtr.Size), 0, 0));
        _pages.Measure(groups);
        if (_pageCount != 0)
        {
            groups.Add(new TextAllocationShape(1, 0, 32, _pages.Get(0)!.Length));
            if (_pageCount > 1)
            {
                groups.Add(new TextAllocationShape(_pageCount - 1, 0, 32, PageSlots));
            }
        }

        for (var earlier = _earlierFirst; earlier is not null; earlier = earlier.Earlier)
        {
            groups.Add(new TextAllocationShape(1, 2 * IntPtr.Size, 0, 0));
            groups.Add(new TextAllocationShape(1, 0, 32, earlier.Backing.Length));
        }
    }
}
