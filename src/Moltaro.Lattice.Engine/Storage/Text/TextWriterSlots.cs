using System.Numerics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Storage.Text;

// Dense writer entries. Keep one empty trailing page available for
// the next write until bounded maintenance returns it. The extra cells
// keep mutable counts off the neighboring lines. Each page is charged to
// the budget before it is made and released when it is dropped; the pages
// emptied by a preparation that was refused or cancelled stay, for the next
// write (docs/design/storage-and-maintenance.md, the memory budget).
internal sealed class TextWriterSlots(MemoryBudget budget)
{
    private const int Padding = 2;
    private const int PageSlots = 64 + 2 * Padding;
    private readonly MemoryBudget _budget = budget;
    private readonly TextPages<TextSlot[]> _pages = new(budget);
    private int _pageCount;

    // The fields above and the one of Count: two references and two counts.
    private static readonly int FieldBytes = 2 * IntPtr.Size + 2 * sizeof(int);

    public int Count { get; private set; }

    public long Bytes => TextAllocationShape.ObjectBytes(FieldBytes) + _pages.Bytes + (_pageCount == 0 ? 0 :
        MemoryBudget.ArrayBytes<TextSlot>(_pages.Get(0)!.Length) + MemoryBudget.ArrayBytes<TextSlot>(PageSlots) * (_pageCount - 1));

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
            var created = _budget.Allocate<TextSlot>(needed);
            try
            {
                _pages.Set(page, created);
            }
            catch
            {
                _budget.Release(created);
                throw;
            }

            _pageCount++;
        }
        else if (previous.Length < needed)
        {
            ResizeFirst(previous, needed);
        }
    }

    private void ReleaseReservation()
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

    // Removes the last entry. A page left empty is released, unless the removal unwinds a preparation
    // that was refused or cancelled, whose capacity stays.
    public void Pop(bool keepsPages = false)
    {
        At((uint)Count) = default;
        Count--;
        if ((Count & 63) == 0 && !keepsPages)
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

    private void ReleaseLastPage()
    {
        var page = (uint)--_pageCount;
        var released = _pages.Get(page)!;
        _pages.Set(page, null);
        _budget.Release(released);
    }

    private void ResizeFirst(TextSlot[] previous, int needed)
    {
        var replacement = _budget.Allocate<TextSlot>(needed);
        previous.AsSpan(0, Math.Min(previous.Length, needed)).CopyTo(replacement);
        _pages.Set(0, replacement);
        _budget.Release(previous);
    }

    public void ReportHeld(List<HeldArray> held)
    {
        for (var page = 0u; page < _pageCount; page++)
        {
            held.Add(new HeldArray(nameof(TextTable), typeof(TextSlot), _pages.Get(page)!.Length));
        }

        _pages.ReportHeld(held);
    }

    private static int FirstCapacity(int entries) => (int)BitOperations.RoundUpToPowerOf2((uint)entries) + 2 * Padding;

    public void Measure(List<TextAllocationShape> groups)
    {
        groups.Add(new TextAllocationShape(1, FieldBytes, 0, 0));
        _pages.Measure(groups);
        if (_pageCount != 0)
        {
            groups.Add(new TextAllocationShape(1, 0, Unsafe.SizeOf<TextSlot>(), _pages.Get(0)!.Length));
            if (_pageCount > 1)
            {
                groups.Add(new TextAllocationShape(_pageCount - 1, 0, Unsafe.SizeOf<TextSlot>(), PageSlots));
            }
        }
    }
}
