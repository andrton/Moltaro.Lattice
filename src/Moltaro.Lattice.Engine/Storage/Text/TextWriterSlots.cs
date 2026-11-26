using System.Numerics;

namespace Moltaro.Lattice.Storage.Text;

// Dense writer entries. Keep one empty trailing page available for
// the next write until bounded maintenance returns it. The extra cells
// keep mutable counts off the neighboring lines.
internal sealed class TextWriterSlots
{
    private const int Padding = 2;
    private const int PageSlots = 64 + 2 * Padding;
    private readonly TextPages<TextSlot[]> _pages = new();
    private int _pageCount;

    public int Count { get; private set; }

    public long Bytes => 32 + _pages.Bytes + (_pageCount == 0 ? 0 :
        24L + 32L * _pages.Get(0)!.Length + (24L + 32L * PageSlots) * (_pageCount - 1));

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
            _pages.Set(page, new TextSlot[needed]);
            _pageCount++;
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
        return true;
    }

    private void ReleaseLastPage() => _pages.Set((uint)--_pageCount, null);

    private void ResizeFirst(TextSlot[] previous, int needed)
    {
        var replacement = new TextSlot[needed];
        previous.AsSpan(0, Math.Min(previous.Length, needed)).CopyTo(replacement);
        _pages.Set(0, replacement);
    }

    private static int FirstCapacity(int entries) => (int)BitOperations.RoundUpToPowerOf2((uint)entries) + 2 * Padding;

    public void Measure(List<TextAllocationShape> groups)
    {
        groups.Add(new TextAllocationShape(1, 16, 0, 0));
        _pages.Measure(groups);
        if (_pageCount != 0)
        {
            groups.Add(new TextAllocationShape(1, 0, 32, _pages.Get(0)!.Length));
            if (_pageCount > 1)
            {
                groups.Add(new TextAllocationShape(_pageCount - 1, 0, 32, PageSlots));
            }
        }
    }
}
