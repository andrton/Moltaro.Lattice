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
// In S04 the table takes no lock, and one thread at a time uses it. The claims and the protection
// of the arena protocol, which make lookup, deletion and reuse safe together, come with S06 to
// S08.
internal sealed class RootTable
{
    // The number of entries in one page.
    public const int EntriesPerPage = 4096;

    // The pages in the order they were added; the directory holds references to them, and growing
    // the directory copies those references, never an entry. Only the first Pages are pages.
    private RootEntry[][] _pages = [];

    // The slots below this were given out at least once. The slots from here to the end of the last
    // page never were; their entries are as the page was allocated, Free at generation 0.
    private int _nextUnusedSlot;

    // Released slots that Add gives out again, the last released first. A retired slot is never
    // here. The stack holds four bytes per free slot, beside the 48 of its entry.
    private readonly Stack<uint> _freeSlots = new();

    // Takes a free slot for a new lifetime of a root of the entity type typeId and returns its
    // handle, which carries the slot's next generation. When no slot is free, the table adds a
    // page; adding a page copies no existing entry. The entry gets the generation and typeId,
    // every other field zero, and the state Prepared: it belongs to a write that has not published
    // it yet.
    public RootHandle Add(uint typeId)
    {
        var slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : TakeUnusedSlot();
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

        Count++;

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
    // reference to that entry while pages are added. ArgumentException when the handle is not
    // current.
    public ref RootEntry Entry(RootHandle handle)
    {
        if (!IsCurrent(handle))
        {
            throw new ArgumentException($"The handle {handle} names no current lifetime of this table.", nameof(handle));
        }

        return ref EntryAt(handle.Slot);
    }

    // Ends the lifetime the handle names, and the handle is no longer current. The entry becomes
    // Free, and Add can give the slot out again with its next generation; a slot released at
    // generation uint.MaxValue becomes Retired instead and is never given out again. Release looks
    // at neither ClaimCount nor State: who may release a lifetime, and when, is the caller's.
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
            // The slot is pushed first: if the stack cannot grow, the lifetime is still current.
            _freeSlots.Push(handle.Slot);
            entry.State = RootStateEnum.Free;
        }

        Count--;
    }

    // How many slots hold a lifetime: given out by Add and not released since. A retired slot is
    // not counted.
    public int Count { get; private set; }

    // How many pages the table has. Times EntriesPerPage, this is the number of its slots.
    public int Pages { get; private set; }

    private ref RootEntry EntryAt(uint slot) =>
        ref _pages[slot / EntriesPerPage][slot % EntriesPerPage];

    // The lowest slot never given out, after adding a page when every slot of the table was.
    private uint TakeUnusedSlot()
    {
        if (_nextUnusedSlot == checked(Pages * EntriesPerPage))
        {
            AddPage();
        }

        return (uint)_nextUnusedSlot++;
    }

    // Adds a page of free entries. The new page is allocated before anything changes, so a failure
    // leaves the table as it was.
    private void AddPage()
    {
        var page = new RootEntry[EntriesPerPage];

        if (Pages == _pages.Length)
        {
            Array.Resize(ref _pages, Math.Max(1, _pages.Length * 2));
        }

        _pages[Pages] = page;
        Pages++;
    }
}
