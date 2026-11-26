using System.Numerics;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Storage.Roots;

// The control entries of the roots of one database, in managed pages of EntriesPerPage entries
// (Q16, "Managed arrays of structs for control entries"; milestone contract, "Native layout and
// allocation"). The table grows by adding a page and never copies or moves an existing entry, so
// a reference to an entry stays a reference to that entry while pages are added.
//
// A slot is named by a RootHandle that carries the slot's generation. The first generation of a
// slot is 1, and every reuse advances it, so a handle of an earlier lifetime never reaches a later
// one. A slot released at generation uint.MaxValue is retired for good.
//
// The table holds at most MostPages pages, 2^31 - 4,096 slots, because Count and the slot numbers
// are counted in an int. A refusal for a new address or its backing leaves the table as it was.
//
// The table takes no lock; its owner says who may call what at the same time. In the database Add
// and Release, which change the list of free slots, are called only by the thread that holds the
// lock of the writes (Engine.Writes.WriteCoordinator), which also looks the key of a write up under
// that lock, while reads inside the protection of reads call EntryOfSlot at the same time. Those read
// only that entry and the directory of pages, which grows by replacing one array with another that
// holds the same pages, so a reader that still has the old directory finds the same entry. A read by
// key learns of a slot through an entry of a primary index, which an insert stores with release
// semantics after it took the slot and wrote its entry (Storage.Hashing.IndexTable.Add), and loads with
// acquire semantics; so the plain stores of Add, of the directory and of the entry come before what the
// reader reads. The capture of a load on a master walks the slots below the extent it took instead,
// whatever lifetime each holds, while the master writes (Engine.Children.Capture): it takes more of an
// entry only once it has loaded the state Live with acquire semantics, which an insert stores last with
// release semantics (Engine.Entities.ChangePublisher.Install), and every other state it skips; Add writes
// the fields of an entry one by one, so that the state is never read torn. The protection of reads keeps
// a slot from being released while a read that could have found it is inside: the entry of a deleted
// root is retired and released only after those reads have left (Engine.Protection.RetiredMemory).
internal sealed class RootTable
{
    // The number of entries in one page.
    public const int EntriesPerPage = 4096;

    // The most pages the table holds: as many whole pages as an int can count slots of.
    private const int MostPages = int.MaxValue / EntriesPerPage;

    private readonly MemoryBudget? _budget;
    private readonly int _pageLimit;

    // The pages in the order they were added; the directory holds references to them, and growing
    // the directory copies those references, never an entry. Only the first Pages are pages.
    private RootEntry[][] _pages = [];

    // Each smaller directory remains owned until a published slot needs a bigger one or an
    // abandoned trailing page permits reuse. There is one entry per doubling, including zero.
    private readonly RootEntry[][][] _smallerDirectories;
    private int _smallerDirectoryCount;
    private int _publishedExtent;

    // The slots below this were given out at least once. The slots from here to the end of the last
    // page never were; their entries are as the page was allocated, Free at generation 0.
    private int _nextUnusedSlot;

    // The list of released slots that Add gives out again, the last released first: the first slot
    // plus one, 0 when no slot is free. A free entry has no entity type, so its TypeId holds the
    // next slot of the list in the same form; Add overwrites it with the type of the new lifetime
    // and zeroes every other field. The list takes no memory beyond the entries, and Release
    // allocates nothing. A retired slot is never in the list.
    //
    // With the count, on lines of their own (RootTableCounts).
    private RootTableCounts _counts;

    public RootTable(MemoryBudget? budget = null, int? pageLimit = null)
    {
        _budget = budget;
        _pageLimit = pageLimit ?? MostPages;
        ArgumentOutOfRangeException.ThrowIfLessThan(_pageLimit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(_pageLimit, MostPages);
        _smallerDirectories = budget?.Allocate<RootEntry[][]>(BitOperations.Log2((uint)_pageLimit) + 2, nameof(RootTable)) ?? [];
    }

    // Takes a free slot for a new lifetime of a root of the entity type typeId and returns its
    // handle, which carries the slot's next generation. When no slot is free, the table adds a
    // page; adding a page copies no existing entry. The entry gets the generation and typeId,
    // every other field zero, and the state Prepared: it belongs to a write that has not published
    // it yet. A new page can be refused for slot address space or for its backing.
    public RootHandle Add(uint typeId)
    {
        uint slot;

        if (_counts.FreeSlots != 0)
        {
            slot = _counts.FreeSlots - 1;
            _counts.FreeSlots = EntryAt(slot).TypeId;
        }
        else
        {
            slot = TakeUnusedSlot();
        }

        ref var entry = ref EntryAt(slot);

        // A released slot keeps the generation of its last lifetime, and a slot never given out has
        // generation 0, so the next lifetime has the next generation, 1 for the first. A slot at
        // uint.MaxValue was retired and is never taken, so the generation cannot wrap.
        var generation = entry.Generation + 1;

        // Field by field, each an aligned word written whole, and not as one assignment of the struct, which
        // the compiler may copy in pieces of its choosing: the capture of a load reads the state of any slot
        // while this runs (Engine.Children.Capture) and has to find it Free or Prepared, never a torn value.
        // The state first; neither it nor the other fields are Live, which is the only state the capture
        // takes more of.
        entry.State = RootStateEnum.Prepared;
        entry.Root = default;
        entry.LastMutationSequence = 0;
        entry.KeyDescriptor = default;
        entry.StoredRootId = 0;
        entry.Generation = generation;
        entry.TypeId = typeId;
        entry.Member = 0;

        _counts.Count++;

        return new RootHandle(generation, slot);
    }

    // Whether the handle names the present lifetime of its slot: the slot still has the handle's
    // generation and is neither free nor retired, because Add gave the handle out and it has not
    // been released since. The default handle is never current.
    public bool IsCurrent(RootHandle handle)
    {
        if (handle.Slot >= (uint)_nextUnusedSlot)
        {
            return false;
        }

        ref var entry = ref EntryAt(handle.Slot);

        return entry.Generation == handle.Generation && entry.State is not (RootStateEnum.Free or RootStateEnum.Retired);
    }

    // A reference to the entry of the lifetime the handle names, in its managed page. It stays a
    // reference to that entry while pages are added, and it refers to the lifetime only until
    // Release of the handle: after that the entry is free, or the entry of a later lifetime, and a
    // caller that kept the reference and writes through it damages the table, the list of its free
    // slots included. ArgumentException when the handle is not current.
    public ref RootEntry Entry(RootHandle handle)
    {
        if (!IsCurrent(handle))
        {
            throw new ArgumentException($"The handle {handle} names no current lifetime of this table.", nameof(handle));
        }

        return ref EntryAt(handle.Slot);
    }

    // A reference to the entry in a slot, whatever lifetime it holds at the moment, for a caller that
    // knows by other means which lifetime that is: a primary index, whose entries name the slot of
    // the root entry of each key (Storage.Hashing.IndexTable). The reference has the
    // lifetime Entry describes. ArgumentOutOfRangeException for a slot that was never given out.
    public ref RootEntry EntryOfSlot(uint slot)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, (uint)_nextUnusedSlot);

        return ref EntryAt(slot);
    }

    // Ends the lifetime the handle names, and the handle is no longer current. The entry becomes
    // Free, and Add can give the slot out again with its next generation; a slot released at
    // generation uint.MaxValue becomes Retired instead and is never given out again. Release does not
    // look at the State: who may release a lifetime, and when, is the caller's.
    // ArgumentException when the handle is not current.
    public void Release(RootHandle handle)
    {
        ref var entry = ref Entry(handle);

        if (entry.Generation == uint.MaxValue)
        {
            entry.State = RootStateEnum.Retired;
        }
        else
        {
            entry.State = RootStateEnum.Free;
            entry.TypeId = _counts.FreeSlots;
            _counts.FreeSlots = handle.Slot + 1;
            _budget?.ReusableSpace();
        }

        _counts.Count--;
    }

    // How many slots hold a lifetime: given out by Add and not released since. A retired slot is
    // not counted.
    public int Count => _counts.Count;

    // The slots below this one were given out at least once; every slot that holds a lifetime is
    // below it. For the capture of a load on a master, which walks the slots below it, taken under the
    // lock of the writes as the fence of the capture: every root live at the start of the capture lies
    // below it.
    public int SlotExtent => _nextUnusedSlot;

    // A capture can only have reached a lifetime that published. A root publishing after
    // the capture began arrives in the transaction stream, regardless of the slot it uses.
    public int CaptureExtent => _publishedExtent;

    public void NotePublished(RootHandle handle) =>
        _publishedExtent = Math.Max(_publishedExtent, checked((int)handle.Slot + 1));

    public void ReleasePublishedDirectories()
    {
        if (_smallerDirectoryCount == 0 || _budget is not { IsPreparing: false } budget)
        {
            return;
        }

        var publishedPages = PublishedPages;
        var obsolete = FirstDirectoryFor(publishedPages);
        for (var index = 0; index < obsolete; index++)
        {
            budget.Release(_smallerDirectories[index]);
        }

        if (obsolete == 0)
        {
            return;
        }

        var remain = _smallerDirectoryCount - obsolete;
        Array.Copy(_smallerDirectories, obsolete, _smallerDirectories, 0, remain);
        Array.Clear(_smallerDirectories, remain, obsolete);
        _smallerDirectoryCount = remain;
    }

    private int PublishedPages => (_publishedExtent + EntriesPerPage - 1) / EntriesPerPage;

    private int FirstDirectoryFor(int pages)
    {
        var index = 0;
        while (index < _smallerDirectoryCount && _smallerDirectories[index].Length < pages)
        {
            index++;
        }

        return index;
    }

    // Under Writes after a queued preparation has released its insert lifetimes. A capture
    // or reader can still name a page that ever held a published lifetime. Another preparation
    // or a permanently retired generation can also hold a slot above the published fence.
    public void WithdrawUnusedPages()
    {
        if (_budget is not { IsPreparing: false } budget)
        {
            return;
        }

        var keep = Pages;
        var publishedPages = PublishedPages;
        while (keep > publishedPages && IsWhollyFree(_pages[keep - 1]))
        {
            keep--;
        }

        if (keep == Pages)
        {
            return;
        }

        RemoveFreeSlotsAtOrAbove(keep * EntriesPerPage);
        _nextUnusedSlot = keep * EntriesPerPage;
        while (Pages > keep)
        {
            var index = --Pages;
            var page = _pages[index];
            _pages[index] = null!;
            budget.Release(page);
        }

        var saved = FirstDirectoryFor(keep);
        if (saved != _smallerDirectoryCount)
        {
            var previous = _smallerDirectories[saved];
            var discarded = _pages;
            Array.Copy(discarded, previous, keep);
            Array.Clear(previous, keep, previous.Length - keep);
            _pages = previous;
            budget.Release(discarded);
            while (_smallerDirectoryCount - 1 > saved)
            {
                var unused = _smallerDirectories[--_smallerDirectoryCount];
                _smallerDirectories[_smallerDirectoryCount] = null!;
                budget.Release(unused);
            }

            _smallerDirectories[--_smallerDirectoryCount] = null!;
        }
    }

    private static bool IsWhollyFree(RootEntry[] page)
    {
        foreach (ref readonly var entry in page.AsSpan())
        {
            if (entry.State != RootStateEnum.Free)
            {
                return false;
            }
        }

        return true;
    }

    // How many pages the table has. Times EntriesPerPage, this is the number of its slots.
    public int Pages { get; private set; }

    public static long PageBytes => MemoryBudget.ArrayBytes<RootEntry>(EntriesPerPage);

    public long AddressableSlots => (long)_pageLimit * EntriesPerPage;

    private ref RootEntry EntryAt(uint slot) =>
        ref _pages[slot / EntriesPerPage][slot % EntriesPerPage];

    // The lowest slot never given out, after adding a page when every slot of the table was.
    private uint TakeUnusedSlot()
    {
        if (_nextUnusedSlot == Pages * EntriesPerPage)
        {
            AddPage();
        }

        return (uint)_nextUnusedSlot++;
    }

    // Adds a page of free entries. The new page is allocated before anything changes, so a failure
    // leaves the table as it was.
    private void AddPage()
    {
        if (Pages == _pageLimit)
        {
            throw new BudgetRefusedException(LatticeLimitEnum.RootSlots);
        }

        var previous = _pages;
        var grows = Pages == previous.Length;
        var length = grows ? Math.Min(_pageLimit, Math.Max(1, previous.Length * 2)) : previous.Length;
        var bytes = PageBytes + (grows ? MemoryBudget.ArrayBytes<RootEntry[]>(length) : 0);
        _budget?.Reserve(bytes);

        var directory = previous;
        var index = Pages;
        RootEntry[]? page = null;
        try
        {
            page = new RootEntry[EntriesPerPage];
            _budget?.Own(page, nameof(RootTable));
            if (grows)
            {
                directory = new RootEntry[length][];
                Array.Copy(previous, directory, previous.Length);
                _budget?.Own(directory, nameof(RootTable));
            }

            if (_budget is { IsPreparing: true } budget)
            {
                budget.WhenPreparationEnds(keeps =>
                {
                    if (!keeps)
                    {
                        RemoveNewPage(index, previous, directory, page);
                    }
                });
            }
            else if (grows)
            {
                _budget?.Release(previous);
            }
        }
        catch
        {
            if (page is not null)
            {
                _budget?.Forget(page);
            }

            if (!ReferenceEquals(directory, previous))
            {
                _budget?.Forget(directory);
            }

            _budget?.Release(bytes);
            throw;
        }

        directory[Pages] = page;
        if (grows && _budget is { IsPreparing: true })
        {
            _smallerDirectories[_smallerDirectoryCount++] = previous;
        }

        _pages = directory;
        Pages++;
    }

    private void RemoveNewPage(int index, RootEntry[][] previous, RootEntry[][] directory, RootEntry[] page)
    {
        var first = index * EntriesPerPage;
        RemoveFreeSlotsAtOrAbove(first);
        _nextUnusedSlot = first;
        Pages = index;
        directory[index] = null!;
        _pages = previous;
        _budget!.Release(page);
        if (!ReferenceEquals(directory, previous))
        {
            _smallerDirectories[--_smallerDirectoryCount] = null!;
            _budget.Release(directory);
        }
    }

    private void RemoveFreeSlotsAtOrAbove(int first)
    {
        ref var link = ref _counts.FreeSlots;
        while (link != 0)
        {
            var slot = link - 1;
            ref var next = ref EntryAt(slot).TypeId;
            if (slot >= first)
            {
                link = next;
            }
            else
            {
                link = ref next;
            }
        }
    }
}
