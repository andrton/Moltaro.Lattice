using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Protection;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet), the drain list of the class
// LightEpoch (BumpCurrentEpoch with an action, Drain) of
// libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.cs at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation (see
// THIRD-PARTY-NOTICES.md).
//
// What the writes of one database retired and have not freed yet, and the freeing of it
// (docs/design/arena-protocol.md, "Deferred reclamation"). A write that replaces or deletes a root,
// grows a primary index or replaces a node of a secondary index first unlinks the old thing, so that no
// read that enters afterwards can reach it, and then retires it here, tagged with the current epoch of
// ReadEpochs. It is freed once no read that entered with that epoch or an earlier one is inside: an old
// root and the root of a deleted entity go back to the arena, the entry of a deleted entity to the free
// slots of the table of root entries, a replaced table of an index is let go of, and a replaced node of
// an index releases the references of its shared keys and goes back to the arena
// (docs/design/ordered-index.md, "The retired node"). Until then a retired root or node counts in
// ArenaCounters.RetiredBytes, a retired entry in RootTable.Count, and a retired table in IndexBytes.
//
// Who calls it, and when. Only the thread that holds the lock of the writes (Writes.WriteExecutor),
// because the arena, the table of root entries and the indexes take no lock and only that thread
// changes them. A read never frees. The periodic maintenance worker drains a bounded safe prefix
// even when there are no further writes. Synchronous drains still happen in these operations:
// - after every publication of a position, a write on a master or a transaction on a child, and of a
//   root of a load, the writer looks once enough was counted since its last look (AfterWrite):
//   as many things as ReadEpochs has slots, or as many bytes as one segment of the arena. Each look
//   moves the epoch on and reads every slot, which costs about what reading the slots costs, so the
//   look is made once for as many retired things as there are slots: about one read of a slot for
//   each, measured at 50 to 850 ns a look. The bytes bound what one look can leave unfreed to one
//   segment's worth beside what reads hold, whatever the size of the roots.
//   They are derived from the table and the arena, not options.
//   A look sets both counts to zero.
//   What a look could not free, because a read could still reach it, stays retired: in RetiredBytes
//   and in the list. A later look frees it, once that read has left.
//   A look that leaves retired things behind arms a second trigger: until a look leaves nothing
//   behind, a published write that retires nothing counts as one thing. Whether a publication retired
//   nothing is noted here, not told by the writer: anything retired since the publication before it
//   counts, so an insert whose reservation grew a primary index, and retired its old table, is a write
//   that retired something.
//   Without such a look a write that retires nothing counts nothing, also while something retired
//   waits below the thresholds, so a load without a held read looks as often as it would without
//   that trigger, and a load that retires nothing never looks.
//   So after a look that left something behind, the next look comes within one window of writes of
//   any kind, and what waits between two looks is what reads held at the last look and at most one
//   window more: bounded by the two thresholds beside what reads hold (docs/design/arena-protocol.md,
//   "Deferred reclamation").
//   What a look could not free does not count toward the next look: under a read held long, it would
//   keep the counts at the thresholds, and every write would look.
//   The bytes of a retired root are the slots of its own blocks and those of the entry of every
//   Dictionary text it names: an entry whose last reference lies in retired roots stays live until the
//   drain frees them, and is freed with them, so it waits for the same look. A text that other roots
//   share is counted for each root that names it; that only makes a look come earlier, and a look
//   frees nothing a read can still reach. A retired node counts its slot and the slots of the text
//   entries its shared keys name, by the same rule.
// - EngineDatabase.DrainRetired, which a test calls before it counts and the close after it waited
//   for the reads.
// - a write that the arena refused, which drains, and in its resolution waits, outside the lock, for
//   the reads that hold what is retired (ReadsToWaitFor) and drains again, before it is refused
//   (Writes.WriteCoordinator, "The refusal").
//
// Room. A write that will retire something reserves a place for it while it is prepared (Reserve),
// beside the places the writes prepared before it and not published yet reserved, so that retiring it
// after its publication allocates nothing; the place is taken when it retires and given back when the
// write is withdrawn (Release). The tree of a secondary index reserves as many places as it reserves
// nodes (docs/design/ordered-index.md, "The reservation").
//
// What LightEpoch does and this does not: its list has 16 places and a thread that finds it full
// waits until an action runs; here the list grows, so a write never waits for a read to free a
// place. Its actions run on whichever thread drains; here the thread must hold the writes lock.
internal sealed class RetiredMemory
{
    // A bounded first allocation for maintenance. A nonempty queue is never copied by a step.
    private const int RelocationSeed = 4;

    private readonly ReadEpochs _epochs;

    private readonly Arena _arena;

    private readonly RootTable _roots;

    private readonly Action<int, int, int>? _beforePublishedTrim;

    private readonly Action<int>? _observeBackingRefusal;

    // In the order they were retired, and so in the order of their epochs.
    private readonly Queue<RetiredItem> _items = new();

    // Only the FIFO head can be partial. A safe head stays safe across yields,
    // even when later publications enqueue younger items.
    private RootReclamation? _rootWork;

    private NodeReclamation _nodeWork;

    private bool _headActive;

    private bool _releaseHandle;

    // The things and the bytes retired since the last look, a write that retired nothing counted as one
    // thing while the count of such writes is armed, and how many of each make a look (see the class).
    private readonly int _itemsPerLook;

    private readonly long _bytesPerLook;

    private int _itemsSinceLook;

    private long _bytesSinceLook;

    // Whether the last look left retired things behind, which arms the count of the writes that retire
    // nothing until a look leaves nothing behind (see the class).
    private bool _leftBehind;

    // Whether anything was retired since the last publication (AfterWrite): by that publication, or by
    // a preparation before it, as an insert whose reservation grew a primary index.
    private bool _retiredSincePublication;

    // The epoch of the last thing retired.
    private long _newest;

    // The capacity of the list, as EnsureCapacity reports it.
    private int _capacity;

    // Growth for reservations stays provisional until actual retirements need it.
    // Earlier arrays hold no items: a withdrawal copies the current FIFO into
    // the smallest backing still needed by the published or prepared work.
    private int _publishedCapacity;

    private BackingHistory<RetiredItem[]>? _earlier;

    // The places reserved for things that writes prepared and not published yet will retire: while a
    // group is in the persistence step, later writes are prepared and published after it, in their order
    // (Writes.WriteCoordinator).
    private int _reserved;

    // What the next write the arena refused runs at the first wait of its resolution, and on a child what
    // the next transaction its arena refused twice runs before its applier waits for the reads, for the
    // tests of the engine (EngineDatabase.ArmWaitSwitch); null when no test armed it, and only
    // a refused write or transaction loads it. The one field of this class written and read outside the
    // lock of the writes: a test arms it from its own thread and the waiter takes it before it waits,
    // outside every lock, so it is written with release semantics and taken by an interlocked exchange,
    // which runs it once.
    private Action? _waitSwitch;

    public RetiredMemory(ReadEpochs epochs, Arena arena, RootTable roots, Action<int, int, int>? beforePublishedTrim = null,
        Action<int>? observeBackingRefusal = null)
    {
        _epochs = epochs;
        _arena = arena;
        _roots = roots;
        _beforePublishedTrim = beforePublishedTrim;
        _observeBackingRefusal = observeBackingRefusal;
        _itemsPerLook = epochs.SlotCount;
        _bytesPerLook = arena.SegmentSize;
    }

    // The bytes of the retired tables of the primary indexes, counted as an index counts its table.
    public long IndexBytes { get; private set; }

    // Under the writes lock: the full native slot sizes of pending physical relocations.
    public int RelocationBytes { get; private set; }

    // Makes room for one more retired thing beside those reserved before, so that retiring it after a
    // publication allocates nothing and cannot fail. Called before the publication; the reservation
    // ends when the thing is retired, or with Release for a write that is withdrawn.
    public void Reserve() => Reserve(1);

    // Makes room for that many more retired things, as Reserve does for one; nothing is reserved when the
    // managed heap cannot hold the room.
    public void Reserve(int places)
    {
        EnsureCapacity(_items.Count + _reserved + places);

        _reserved += places;
    }

    // Under the writes lock: charge the full new slot and reserve a FIFO place without copying
    // queued items. The charge follows the old slot at publication until it is actually freed.
    public bool TryReserveRelocated(int slotBytes = 0)
    {
        if (slotBytes > Arena.MaxBlockSize - RelocationBytes)
        {
            return false;
        }

        if (_capacity == 0)
        {
            try
            {
                EnsureCapacity(RelocationSeed);
            }
            catch (Exception exception) when (exception is OutOfMemoryException or BudgetRefusedException)
            {
                return false;
            }
        }

        if (_items.Count + _reserved == _capacity)
        {
            return false;
        }

        _reserved++;
        RelocationBytes += slotBytes;
        return true;
    }

    public void ReleaseRelocation(int slotBytes)
    {
        Release();
        RelocationBytes -= slotBytes;
    }

    // Gives back the place a write reserved and does not use, because it was withdrawn.
    public void Release() => Release(1);

    // Gives back that many places reserved and not used.
    public void Release(int places)
    {
        _reserved -= places;
        ReturnUnusedBacking();
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _capacity)
        {
            return;
        }

        if (_arena.Budget is not { } budget)
        {
            _publishedCapacity = _capacity = _items.EnsureCapacity(needed);
            return;
        }

        var previous = BudgetCollections.Items(_items);
        var capacity = BudgetCollections.CapacityFor(_capacity, needed);
        RetiredItem[]? replacement = null;
        try
        {
            replacement = budget.Allocate<RetiredItem>(capacity, nameof(RetiredMemory));
            if (previous.Length != 0)
            {
                _earlier = BackingHistory<RetiredItem[]>.Keep(previous, _earlier, budget, nameof(RetiredMemory));
            }
        }
        catch (Exception exception)
        {
            if (replacement is not null)
            {
                budget.Release(replacement);
            }

            if (exception is BudgetRefusedException)
            {
                _observeBackingRefusal?.Invoke(needed);
            }

            throw;
        }

        _items.UseBacking(replacement);
        _capacity = capacity;
        Array.Clear(previous);
    }

    private void ReturnUnusedBacking()
    {
        var needed = Math.Max(_publishedCapacity, _items.Count + _reserved);
        if (needed >= _capacity || _arena.Budget is not { } budget)
        {
            return;
        }

        RetiredItem[]? replacement = null;
        while (_earlier is { } earlier && earlier.Backing.Length >= needed)
        {
            if (replacement is not null)
            {
                budget.Release(replacement);
            }

            replacement = earlier.Backing;
            _earlier = earlier.Drop(budget);
        }

        if (needed == 0)
        {
            if (replacement is not null)
            {
                budget.Release(replacement);
            }

            replacement = [];
        }

        if (replacement is not null)
        {
            var previous = BudgetCollections.Items(_items);
            _items.UseBacking(replacement);
            _capacity = replacement.Length;
            budget.Release(previous);
        }
    }

    private void KeepPublishedCapacity()
    {
        var keep = _capacity;
        ref var older = ref _earlier;
        while (older is { } earlier && earlier.Backing.Length >= _items.Count)
        {
            keep = earlier.Backing.Length;
            older = ref earlier.Earlier;
        }

        _publishedCapacity = keep;
        if (_arena.Budget is { } budget)
        {
            while (older is { } earlier)
            {
                older = earlier.Drop(budget);
                budget.Release(earlier.Backing);
            }
        }
    }

    // Retires the root a replace replaced: its blocks become retired in the arena (RootStorage.Retire).
    public void RetireRoot(RootLayout layout, ArenaAddress root) =>
        Add(RetiredKindEnum.Root, layout, root, default, RetireBlocks(layout, root));

    // Retires the root of a deleted entity and its entry, which stays counted until it is released.
    public void RetireDeleted(RootLayout layout, RootHandle handle, ArenaAddress root) =>
        Add(RetiredKindEnum.DeletedRoot, layout, root, handle, RetireBlocks(layout, root));

    // Retires the old slot after publishing a physical move. Its owner and other edges,
    // if any, belong to the replacement. The drain test can retire an already allocated
    // block without a copy reservation.
    public void RetireRelocated(ArenaAddress source, int reservedBytes = 0)
    {
        var slotBytes = _arena.Retire(source);
        RelocationBytes += slotBytes - reservedBytes;
        Add(RetiredKindEnum.RelocatedBlock, null, source, default, slotBytes);
    }

    // Retires a table of an index that a larger one replaced, with its bytes.
    public void RetireTable(object table, long bytes)
    {
        IndexBytes += bytes;
        Add(RetiredKindEnum.IndexTable, table, default, default, bytes);
    }

    // Retires a node of the index of the layout that a write replaced: its block becomes retired in the
    // arena, and it counts its slot and the text entries its shared keys name (NodeStorage.Retire).
    public void RetireNode(IndexLayout layout, ArenaAddress node) =>
        Add(RetiredKindEnum.IndexNode, layout, node, default, layout.NodeBytes + NodeStorage.Retire(_arena, layout, node));

    // After the publication of one position, or of one root of a load: looks when enough was counted
    // since the last look, a publication after which nothing was retired since the one before it
    // counting one thing while the last look left something behind (see the class).
    public void AfterWrite()
    {
        if (_leftBehind && !_retiredSincePublication)
        {
            _itemsSinceLook++;
        }

        _retiredSincePublication = false;

        if (_itemsSinceLook >= _itemsPerLook || _bytesSinceLook >= _bytesPerLook)
        {
            Drain(inspectPublishedTrim: _beforePublishedTrim);
        }
    }

    // Shares one horizon across fresh heads, and carries an already safe
    // partial head without a scan. The caller holds the writes lock.
    public int DrainStep(int workLimit, Action? beforeRootReclamationAllocation = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workLimit);

        var spent = 0;
        long? safe = null;
        while (spent < workLimit && _items.TryPeek(out var item))
        {
            if (!_headActive)
            {
                safe ??= _epochs.Advance();
                if (item.Epoch > safe.Value)
                {
                    break;
                }

                if (_rootWork is null && item.Kind is RetiredKindEnum.Root or RetiredKindEnum.DeletedRoot)
                {
                    try
                    {
                        beforeRootReclamationAllocation?.Invoke();
                        _rootWork = new RootReclamation(_arena);
                    }
                    catch (OutOfMemoryException)
                    {
                        // No cursor or root has changed. Charge this attempt and
                        // leave the same FIFO head for a later turn.
                        return spent + 1;
                    }
                }
            }

            if (StepHead(item))
            {
                FinishHead();
            }

            spent++;
        }

        if (_items.Count == 0)
        {
            ForgetLook();

            if (spent < workLimit && CanTrimEmptyQueue())
            {
                TryTrimEmptyQueue();
                spent++;
            }
        }

        return spent;
    }

    // One unit belongs to the head: open its cursor, inspect one part, release
    // one slot or handle, or drop one managed table. No later item can bypass it.
    private bool StepHead(in RetiredItem item)
    {
        if (!_headActive)
        {
            switch (item.Kind)
            {
                case RetiredKindEnum.Root:
                case RetiredKindEnum.DeletedRoot:
                    _rootWork!.Start((RootLayout)item.Owner!, item.Address);
                    _headActive = true;
                    return false;

                case RetiredKindEnum.IndexNode:
                    _headActive = true;
                    return false;

                default:
                    Free(item);
                    return true;
            }
        }

        if (item.Kind == RetiredKindEnum.IndexNode)
        {
            return _nodeWork.Step(_arena, (IndexLayout)item.Owner!, item.Address);
        }

        if (_releaseHandle)
        {
            _roots.Release(item.Handle);
            return true;
        }

        if (!_rootWork!.Step())
        {
            return false;
        }

        _releaseHandle = item.Kind == RetiredKindEnum.DeletedRoot;
        return !_releaseHandle;
    }

    private void FinishHead()
    {
        _items.Dequeue();
        _nodeWork = default;
        _headActive = false;
        _releaseHandle = false;
    }

    private void ForgetLook()
    {
        _itemsSinceLook = 0;
        _bytesSinceLook = 0;
        _leftBehind = false;
        _retiredSincePublication = false;
    }

    // Frees every retired thing that no read in progress can reach without waiting.
    // Refusal to shrink the empty queue leaves its backing available for future retirements.
    public void Drain(Action<int, int, int>? inspectPublishedTrim = null)
    {
        if (DrainAndCanTrim())
        {
            TryTrimEmptyQueue(inspectPublishedTrim);
        }
    }

    // After the admitted operations and protected reads have left, the close reports a
    // failure from the final drain. It frees the arena even when the drain fails.
    public void DrainAtClose(Action? beforeTrim)
    {
        if (DrainAndCanTrim())
        {
            beforeTrim?.Invoke();
            TrimEmptyQueue();
        }
    }

    private bool DrainAndCanTrim()
    {
        _itemsSinceLook = 0;
        _bytesSinceLook = 0;

        if (_items.Count == 0)
        {
            return false;
        }

        if (_headActive)
        {
            var partial = _items.Peek();
            while (!StepHead(partial))
            {
            }

            FinishHead();
        }

        if (_items.Count != 0)
        {
            var safe = _epochs.Advance();
            while (_items.TryPeek(out var item) && item.Epoch <= safe)
            {
                _items.Dequeue();
                Free(item);
            }
        }

        _leftBehind = _items.Count > 0;

        // The list keeps the capacity of its peak, 48 bytes a place, which reads held long can make
        // large: a read held for a second beside a million writes a second leaves a million places.
        // Once it is empty and its capacity is more than 64 looks' worth (8,192 places, 384 KiB with
        // 128 slots), it lets the places go, unless places are reserved for writes not published yet;
        // emptied, the trim replaces its backing without copying any items. It grows again by doubling
        // before a publication (Reserve), when retiring needs it. The margin keeps an ordinary load, which holds
        // one or two looks' worth, from trimming and growing again: only a peak of 64 looks' worth
        // does, and its growth costs about as much as it retired.
        return CanTrimEmptyQueue();
    }

    private bool CanTrimEmptyQueue() =>
        _items.Count == 0 && _reserved == 0 && _capacity > 64 * _itemsPerLook;

    private void TryTrimEmptyQueue(Action<int, int, int>? inspectPublishedTrim = null)
    {
        try
        {
            inspectPublishedTrim?.Invoke(_items.Count, _reserved, _capacity);
            TrimEmptyQueue();
        }
        catch (OutOfMemoryException)
        {
            // The empty-array allocation was refused. Keep the backing for reuse
            // or a later trim.
        }
    }

    private void TrimEmptyQueue()
    {
        var backing = _arena.Budget is null ? null : BudgetCollections.Items(_items);
        _items.TrimExcess();
        if (backing is not null)
        {
            _arena.Budget!.Release(backing);
        }

        _capacity = 0;
        _publishedCapacity = 0;
    }

    // For a write the arena refused, under the lock of the writes: the epoch below which the reads
    // still inside could reach something of the arena retired so far, or 0 when none can, because
    // nothing of the arena is retired or no read that entered before the newest retirement is inside.
    // The write waits for those reads outside every lock (ReadEpochs.WaitForReadsBefore), so that a
    // drain after it frees everything retired up to now.
    public long ReadsToWaitFor()
    {
        if (_items.Count == 0)
        {
            return 0;
        }

        // A read that enters from now on announces an epoch after the newest tag.
        return _epochs.Advance() < _newest ? _newest + 1 : 0;
    }

    public bool HasReusableRootSlot()
    {
        foreach (var item in _items)
        {
            if (item.Kind == RetiredKindEnum.DeletedRoot && item.Handle.Generation < uint.MaxValue)
            {
                return true;
            }
        }

        return false;
    }

    // Arms the switch of the tests for a refused write (_waitSwitch): the next one runs the action once,
    // at the first wait of its resolution.
    public void ArmWaitSwitch(Action action) => Volatile.Write(ref _waitSwitch, action);

    // Runs the switch of the tests for a refused write once, when one is armed, outside every lock: for a
    // write at the first wait of its resolution (Writes.WriteCoordinator), and on a child for a transaction
    // its arena refused twice, once its applier knows it waits for the reads that hold retired memory and
    // before it waits (Child.Loading.ReceivedChanges.Apply).
    public void RunWaitSwitch()
    {
        if (_waitSwitch is not null)
        {
            Interlocked.Exchange(ref _waitSwitch, null)?.Invoke();
        }
    }

    // After the close, once no read and no write is in progress: forgets everything retired. The arena
    // is disposed with every block, retired ones included, and the retired tables are let go of.
    public void Clear()
    {
        _items.Clear();
        _reserved = 0;
        _publishedCapacity = 0;
        ReturnUnusedBacking();
        _rootWork?.Clear();
        _nodeWork = default;
        _headActive = false;
        _releaseHandle = false;
        IndexBytes = 0;
    }

    // Retires the blocks of the root and returns what it counts toward the next look: the slots of
    // its blocks, now retired, and those of the text entries it names (see the class).
    private long RetireBlocks(RootLayout layout, ArenaAddress root)
    {
        var before = _arena.Counters.RetiredBytes;
        var textBytes = RootStorage.Retire(_arena, layout, root);

        return _arena.Counters.RetiredBytes - before + textBytes;
    }

    // Retires a thing into the place its write reserved.
    private void Add(RetiredKindEnum kind, object? owner, ArenaAddress address, RootHandle handle, long bytes)
    {
        _newest = _epochs.Current;
        _items.Enqueue(new RetiredItem(kind, _newest, owner, address, handle, bytes));
        _reserved--;
        if (_items.Count > _publishedCapacity)
        {
            KeepPublishedCapacity();
        }

        if (_earlier is not null && _reserved == 0)
        {
            ReturnUnusedBacking();
        }

        _itemsSinceLook++;
        _bytesSinceLook += bytes;
        _retiredSincePublication = true;
    }

    private void Free(in RetiredItem item)
    {
        switch (item.Kind)
        {
            case RetiredKindEnum.Root:
                RootStorage.Free(_arena, (RootLayout)item.Owner!, item.Address, reclaimingRetired: true);
                break;

            case RetiredKindEnum.DeletedRoot:
                RootStorage.Free(_arena, (RootLayout)item.Owner!, item.Address, reclaimingRetired: true);
                _roots.Release(item.Handle);
                break;

            case RetiredKindEnum.IndexNode:
                NodeStorage.Free(_arena, (IndexLayout)item.Owner!, item.Address);
                break;

            case RetiredKindEnum.RelocatedBlock:
                _arena.Free(item.Address);
                RelocationBytes -= (int)item.Bytes;
                break;

            default:
                (item.Owner as IndexTable)?.ReleaseBacking();
                IndexBytes -= item.Bytes;
                break;
        }
    }
}
