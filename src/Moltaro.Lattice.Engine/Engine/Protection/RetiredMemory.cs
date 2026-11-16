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
// - after every write, the writer looks once enough was retired since its last look (AfterWrite):
//   as many things as ReadEpochs has slots, or as many bytes as one segment of the arena. Each look
//   moves the epoch on and reads every slot, which costs about what reading the slots costs, so the
//   look is made once for as many retired things as there are slots: about one read of a slot for
//   each, measured at 50 to 850 ns a look in phase 1 of S08. The bytes bound what one look can
//   leave unfreed to one segment's worth beside what reads hold, whatever the size of the roots.
//   They are derived from the table and the arena, not options.
// - EngineDatabase.DrainRetired, which a test calls before it counts and the close after it waited
//   for the reads.
// - a write that the arena refused, which drains, waits for the reads that hold the rest and drains
//   again before it is refused (WaitForReads).
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

    // The things and the bytes retired since the last look, and how many of each make a look.
    private readonly int _itemsPerLook;

    private readonly long _bytesPerLook;

    private int _itemsSinceLook;

    private long _bytesSinceLook;

    // The epoch of the last thing retired.
    private long _newest;

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

    // Makes room for one more retired thing, so that retiring it after a publication allocates
    // nothing and cannot fail. Called before the publication.
    public void Reserve() => _items.EnsureCapacity(_items.Count + 1);

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

    // After a write: looks when enough was retired since the last look.
    public void AfterWrite()
    {
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
    }

    // For a write the arena refused: returns once no read that could reach anything retired so far is
    // inside, so that a drain after it frees everything. It does nothing when nothing of the arena is
    // retired. The wait is finite (ReadEpochs.WaitForReadsBefore), has no timeout and no option, and
    // the token of the write cancels it.
    public void WaitForReads(CancellationToken cancellationToken)
    {
        if (_arena.Counters.RetiredBytes == 0)
        {
            return;
        }

        // A read that enters from now on announces an epoch after the newest tag.
        if (_epochs.Advance() < _newest)
        {
            _epochs.WaitForReadsBefore(_newest + 1, cancellationToken);
        }
    }

    // After the close, once no read and no write is in progress: forgets everything retired. The arena
    // is disposed with every block, retired ones included, and the retired tables are let go of.
    public void Clear()
    {
        _items.Clear();
        IndexBytes = 0;
    }

    private long RetireBlocks(RootLayout layout, ArenaAddress root)
    {
        var before = _arena.Counters.RetiredBytes;

        RootStorage.Retire(_arena, layout, root);

        return _arena.Counters.RetiredBytes - before;
    }

    private void Add(RetiredKindEnum kind, RootLayout? layout, ArenaAddress root, RootHandle handle, object? table, long bytes)
    {
        _newest = _epochs.Current;
        _items.Enqueue(new RetiredItem(kind, _newest, layout, root, handle, table, bytes));
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
