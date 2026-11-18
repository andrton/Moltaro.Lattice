using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Protection;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet), the drain list of the class
// LightEpoch (BumpCurrentEpoch with an action, Drain) of
// libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.cs at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation (see
// THIRD-PARTY-NOTICES.md).
//
// What the writes of one database retired and have not freed yet, and the freeing of it (arena
// protocol, "Deferred reclamation, 16 November 2026"; brief of S08, "Counters and the drain, part
// 1"). A write that replaces or deletes a root, or grows a primary index, first unlinks the old
// thing, so that no read that enters afterwards can reach it, and then retires it here, tagged with
// the current epoch of ReadEpochs. It is freed once no read that entered with that epoch or an
// earlier one is inside: an old root and the root of a deleted entity go back to the arena, the
// entry of a deleted entity to the free slots of the table of root entries, and a replaced table of
// an index is let go of. Until then a retired root counts in ArenaCounters.RetiredBytes, a retired
// entry in RootTable.Count, and a retired table in IndexBytes.
//
// Who calls it, and when. Only the thread that holds the lock of the writes (Writes.WriteExecutor),
// because the arena, the table of root entries and the indexes take no lock and have one writer.
// A read never frees. Freeing happens on the path of the operations of the database, and no thread
// or timer is started for it:
// - after every group of writes published, the writer looks once enough was counted since its last
//   look (AfterWrite):
//   as many things as ReadEpochs has slots, or as many bytes as one segment of the arena. Each look
//   moves the epoch on and reads every slot, which costs about what reading the slots costs, so the
//   look is made once for as many retired things as there are slots: about one read of a slot for
//   each, measured at 50 to 850 ns a look in phase 1 of S08. The bytes bound what one look can
//   leave unfreed to one segment's worth beside what reads hold, whatever the size of the roots.
//   They are derived from the table and the arena, not options.
//   A look sets both counts to zero.
//   What a look could not free, because a read could still reach it, stays retired: in RetiredBytes
//   and in the list. A later look frees it, once that read has left.
//   A look that leaves retired things behind arms a second trigger: until a look leaves nothing
//   behind, a published write that retires nothing counts as one thing.
//   Without such a look a write that retires nothing counts nothing, also while something retired
//   waits below the thresholds, so a load without a held read looks as often as it would without
//   that trigger, and a load that retires nothing never looks.
//   So after a look that left something behind, the next look comes within one window of writes of
//   any kind, and what waits between two looks is what reads held at the last look and at most one
//   window more: bounded by the two thresholds beside what reads hold (docs/design/arena-protocol.md,
//   "Deferred reclamation, 16 November 2026").
//   What a look could not free does not count toward the next look: under a read held long, it would
//   keep the counts at the thresholds, and every write would look.
//   The bytes of a retired root are the slots of its own blocks and those of the entry of every
//   Dictionary text it names: an entry whose last reference lies in retired roots stays live until the
//   drain frees them, and is freed with them, so it waits for the same look. A text that other roots
//   share is counted for each root that names it; that only makes a look come earlier, and a look
//   frees nothing a read can still reach.
// - EngineDatabase.DrainRetired, which a test calls before it counts and the close after it waited
//   for the reads.
// - a write that the arena refused, which drains, and in its resolution waits, outside the lock, for
//   the reads that hold what is retired (ReadsToWaitFor) and drains again, before it is refused
//   (Writes.WriteCoordinator, "The refusal").
//
// Room. A write that will retire something reserves a place for it while it is prepared (Reserve),
// beside the places the writes prepared before it and not published yet reserved, so that retiring it
// after its publication allocates nothing; the place is taken when it retires and given back when the
// write is withdrawn (Release).
//
// What LightEpoch does and this does not: its list has 16 places and a thread that finds it full
// waits until an action runs; here the list grows, so a write never waits for a read to free a
// place. Its actions run on whichever thread drains; here only the writer frees.
internal sealed class RetiredMemory
{
    private readonly ReadEpochs _epochs;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    // In the order they were retired, and so in the order of their epochs.
    private readonly Queue<RetiredItem> _items = new();

    // The things and the bytes retired since the last look, a write that retired nothing counted as one
    // thing while the count of such writes is armed, and how many of each make a look (see the class).
    private readonly int _itemsPerLook;

    private readonly long _bytesPerLook;

    private int _itemsSinceLook;

    private long _bytesSinceLook;

    // Whether the last look left retired things behind, which arms the count of the writes that retire
    // nothing until a look leaves nothing behind (see the class).
    private bool _leftBehind;

    // The epoch of the last thing retired.
    private long _newest;

    // The capacity of the list, as EnsureCapacity reports it.
    private int _capacity;

    // The places reserved for things that writes prepared and not published yet will retire: while a
    // group is in the storage step, later writes are prepared and published after it, in their order
    // (Writes.WriteCoordinator).
    private int _reserved;

    public RetiredMemory(ReadEpochs epochs, Arena arena, RootTable roots)
    {
        _epochs = epochs;
        _arena = arena;
        _roots = roots;
        _itemsPerLook = epochs.SlotCount;
        _bytesPerLook = arena.SegmentSize;
    }

    // The bytes of the retired tables of the primary indexes, counted as an index counts its table.
    public long IndexBytes { get; private set; }

    // Makes room for one more retired thing beside those reserved before, so that retiring it after a
    // publication allocates nothing and cannot fail. Called before the publication; the reservation
    // ends when the thing is retired, or with Release for a write that is withdrawn.
    public void Reserve()
    {
        _capacity = _items.EnsureCapacity(_items.Count + _reserved + 1);
        _reserved++;
    }

    // Gives back the place a write reserved and does not use, because it was withdrawn.
    public void Release() => _reserved--;

    // Retires the root a replace replaced: its blocks become retired in the arena (RootStorage.Retire).
    public void RetireRoot(RootLayout layout, ArenaAddress root) =>
        Add(RetiredKindEnum.Root, layout, root, default, table: null, RetireBlocks(layout, root));

    // Retires the root of a deleted entity and its entry, which stays counted until it is released.
    public void RetireDeleted(RootLayout layout, RootHandle handle, ArenaAddress root) =>
        Add(RetiredKindEnum.DeletedRoot, layout, root, handle, table: null, RetireBlocks(layout, root));

    // Retires a table of an index that a larger one replaced, with its bytes.
    public void RetireTable(object table, long bytes)
    {
        IndexBytes += bytes;
        Add(RetiredKindEnum.IndexTable, layout: null, default, default, table, bytes);
    }

    // After a publication, with the number of its writes that retired nothing: looks when enough was
    // counted since the last look, those writes counting one thing each while the last look left
    // something behind (see the class).
    public void AfterWrite(int retiredNothing)
    {
        if (_leftBehind)
        {
            _itemsSinceLook += retiredNothing;
        }

        if (_itemsSinceLook >= _itemsPerLook || _bytesSinceLook >= _bytesPerLook)
        {
            Drain();
        }
    }

    // Frees every retired thing that no read in progress can reach, and returns; it does not wait.
    // With no read in progress it leaves nothing retired.
    public void Drain()
    {
        _itemsSinceLook = 0;
        _bytesSinceLook = 0;

        if (_items.Count == 0)
        {
            return;
        }

        var safe = _epochs.Advance();

        while (_items.TryPeek(out var item) && item.Epoch <= safe)
        {
            _items.Dequeue();
            Free(item);
        }

        _leftBehind = _items.Count > 0;

        // The list keeps the capacity of its peak, 56 bytes a place, which reads held long can make
        // large: a read held for a second beside a million writes a second leaves a million places.
        // Once it is empty and its capacity is more than 64 looks' worth (8,192 places, 448 KiB with
        // 128 slots), it lets the places go, unless places are reserved for writes not published yet;
        // emptied, the list allocates nothing for that, and it grows again by doubling, before a
        // publication (Reserve), when retiring needs it. The margin keeps an ordinary load, which holds
        // one or two looks' worth, from trimming and growing again: only a peak of 64 looks' worth
        // does, and its growth costs about as much as it retired.
        if (_items.Count == 0 && _reserved == 0 && _capacity > 64 * _itemsPerLook)
        {
            _items.TrimExcess();
            _capacity = 0;
        }
    }

    // For a write the arena refused, under the lock of the writes: the epoch below which the reads
    // still inside could reach something of the arena retired so far, or 0 when none can, because
    // nothing of the arena is retired or no read that entered before the newest retirement is inside.
    // The write waits for those reads outside every lock (ReadEpochs.WaitForReadsBefore), so that a
    // drain after it frees everything retired up to now.
    public long ReadsToWaitFor()
    {
        if (_arena.Counters.RetiredBytes == 0)
        {
            return 0;
        }

        // A read that enters from now on announces an epoch after the newest tag.
        return _epochs.Advance() < _newest ? _newest + 1 : 0;
    }

    // After the close, once no read and no write is in progress: forgets everything retired. The arena
    // is disposed with every block, retired ones included, and the retired tables are let go of.
    public void Clear()
    {
        _items.Clear();
        IndexBytes = 0;
    }

    // Retires the blocks of the root and returns what it counts toward the next look: the slots of
    // its blocks, now retired, and those of the text entries it names (see the class).
    private long RetireBlocks(RootLayout layout, ArenaAddress root)
    {
        var before = _arena.Counters.RetiredBytes;

        RootStorage.Retire(_arena, layout, root);

        return _arena.Counters.RetiredBytes - before + RootStorage.TextBytes(_arena, layout, root);
    }

    // Retires a thing into the place its write reserved.
    private void Add(RetiredKindEnum kind, RootLayout? layout, ArenaAddress root, RootHandle handle, object? table, long bytes)
    {
        _newest = _epochs.Current;
        _items.Enqueue(new RetiredItem(kind, _newest, layout, root, handle, table, bytes));
        _reserved--;
        _itemsSinceLook++;
        _bytesSinceLook += bytes;
    }

    private void Free(in RetiredItem item)
    {
        switch (item.Kind)
        {
            case RetiredKindEnum.Root:
                RootStorage.Free(_arena, item.Layout!, item.Root);
                break;

            case RetiredKindEnum.DeletedRoot:
                RootStorage.Free(_arena, item.Layout!, item.Root);
                _roots.Release(item.Handle);
                break;

            default:
                IndexBytes -= item.Bytes;
                break;
        }
    }
}
