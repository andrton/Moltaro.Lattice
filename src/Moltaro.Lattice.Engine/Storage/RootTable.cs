namespace Moltaro.Lattice.Storage;

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
// are counted in an int. Add that would need one more page throws InvalidOperationException, and
// the table stays as it was.
//
// The table takes no lock; its owner says who may call what at the same time. In the database Add
// and Release, which change the list of free slots, are called only by the thread that holds the
// lock of the writes (Engine.Writes.WriteExecutor), while reads inside the protection of reads call
// EntryOfSlot at the same time for slots a primary index named to them. Those read only that entry
// and the directory of pages, which grows by replacing one array with another that holds the same
// pages, so a reader that still has the old directory finds the same entry. The protection of reads
// keeps a slot from being released while a read that could have found it is inside: the entry of a
// deleted root is retired and released only after those reads have left
// (Engine.Protection.RetiredMemory).
internal sealed class RootTable
{
    // The number of entries in one page.
    public const int EntriesPerPage = 4096;

    // The most pages the table holds: as many whole pages as an int can count slots of.
    private const int MostPages = int.MaxValue / EntriesPerPage;

    // The pages in the order they were added; the directory holds references to them, and growing
    // the directory copies those references, never an entry. Only the first Pages are pages.
    private RootEntry[][] _pages = [];

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

    // Takes a free slot for a new lifetime of a root of the entity type typeId and returns its
    // handle, which carries the slot's next generation. When no slot is free, the table adds a
    // page; adding a page copies no existing entry. The entry gets the generation and typeId,
    // every other field zero, and the state Prepared: it belongs to a write that has not published
    // it yet. InvalidOperationException when the table would need more than MostPages pages.
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

        entry = new RootEntry
        {
            Generation = generation,
            TypeId = typeId,
            State = RootStateEnum.Prepared,
        };

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
    // the root entry of each key (Engine.Indexes.IndexTable). The reference has the
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
        }

        _counts.Count--;
    }

    // How many slots hold a lifetime: given out by Add and not released since. A retired slot is
    // not counted.
    public int Count => _counts.Count;

    // How many pages the table has. Times EntriesPerPage, this is the number of its slots.
    public int Pages { get; private set; }

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
        if (Pages == MostPages)
        {
            throw new InvalidOperationException($"The root table holds its most slots, {MostPages * EntriesPerPage}.");
        }

        var page = new RootEntry[EntriesPerPage];

        if (Pages == _pages.Length)
        {
            Array.Resize(ref _pages, Math.Max(1, _pages.Length * 2));
        }

        _pages[Pages] = page;
        Pages++;
    }
}
