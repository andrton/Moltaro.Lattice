using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.Storage.Hashing;

namespace Moltaro.Lattice.Storage;

// The writer's spare lookup slots (Q17, allocation-and-compaction.md). A slot
// is reachable from its old immutable lookup until the safe FIFO walk cools it.
// Ready slots stay allocated in the arena. A preparation unlinks a slot, and
// either publishes it as active or puts the same grant back on refusal.
//
// One maintenance call considers one owner and gives back at most one slot.
// The lease never belongs to a published descriptor and all changes to
// membership, age and native allocation hold Writes.
//
// The tails are records in one array, charged to the budget of the arena when
// it grows (docs/design/storage-and-maintenance.md, the memory budget): the
// records of one root are linked from the root's entry in a charged set, and
// the records maintenance works on are linked in a list of their own. A record
// is given back to the free records once no root lists it, maintenance has no
// slot of it to return, no preparation edits it and no slot of it is borrowed.
// What one preparation changes stays in plain maps until it is applied or
// abandoned, under the estimate of the write.
internal sealed class CollectionTails(Arena arena, TimeProvider time)
{
    public const int FullSlots = (Arena.MaxBlockSize - BlockHeader.Size) / sizeof(ulong);

    private static readonly TimeSpan Underuse = TimeSpan.FromSeconds(5);

    private CollectionTail[] _records = [];

    // The records ever used: those below are in use or free.
    private int _used;

    // The first free record plus one, 0 when none is free; free records are linked by NextOfRoot.
    private int _free;

    // The first and the last record plus one of the list maintenance works on.
    private int _readyFirst;

    private int _readyLast;

    // For each root that has tails, its first record plus one.
    private readonly ChargedSet<RootTails> _roots = new(new RootComparer(), arena.Budget);

    private readonly Dictionary<ArenaAddress, Dictionary<int, CollectionTailEdit>?> _preparations = [];

    private readonly Dictionary<ArenaAddress, int> _borrowed = [];

    public bool HasOwner(RootHandle handle) => _roots.Contains(new RootTails(handle, 0));

    public bool HasSlots(RootHandle handle, int offset) => Find(handle, offset) is var tail && tail >= 0 && _records[tail].ReadySlots > 0;

    // A sparse root keeps its leases even when none of their fields changes.
    // Only an actual collection edit needs a field table.
    public void PrepareSparse(ArenaAddress root) => _preparations.Add(root, null);

    public void PrepareChange(ArenaAddress root, RootHandle handle, int offset, ArenaAddress former, int active, int need)
    {
        var tail = Find(handle, offset);
        if (tail < 0 && former.IsNull)
        {
            return;
        }

        var isNew = tail < 0;
        if (isNew)
        {
            tail = AddTail(handle, offset);
        }

        try
        {
            Preparation(root).Add(offset, new(tail, former, active, need, isNew, Ended: false));
        }
        catch
        {
            if (isNew)
            {
                Forget(tail);
            }

            throw;
        }

        _records[tail].PendingEdit = true;
    }

    public void PrepareEnd(ArenaAddress root, RootHandle handle, int offset)
    {
        if (Find(handle, offset) is var tail && tail >= 0)
        {
            Preparation(root).TryAdd(offset, new(tail, default, 0, 0, NewOwner: false, Ended: true));
            _records[tail].PendingEdit = true;
        }
    }

    public void Apply(RootHandle handle, ArenaAddress root)
    {
        if (!_preparations.Remove(root, out var edits))
        {
            // A complete replacement or deletion ends every lease of this generation.
            var probe = new RootTails(handle, 0);
            if (_roots.TryGet(probe, out var owner))
            {
                _roots.Remove(probe);
                for (var next = owner.First; next != 0;)
                {
                    var tail = next - 1;
                    next = _records[tail].NextOfRoot;
                    _records[tail].Listed = false;
                    _records[tail].NextOfRoot = 0;
                    End(tail);
                    ReleaseIfUnused(tail);
                }
            }

            return;
        }

        if (edits is null)
        {
            return;
        }

        foreach (var edit in edits.Values)
        {
            var tail = edit.Tail;
            ref var record = ref _records[tail];
            record.PendingEdit = false;
            if (edit.Ended)
            {
                End(tail);
                Forget(tail);
                continue;
            }

            record.Active = edit.Active;
            record.Need = edit.Need;
            if (!edit.Former.IsNull && record.Empty)
            {
                record.Former = edit.Former;
                record.Transferring = edit.Former;
            }

            // Publishing another logical value does not abandon an admitted
            // shrink. Refresh clears a waiting window at four times need and
            // finishes an active shrink at twice need.
            Refresh(tail);
            ForgetEmpty(tail);
        }
    }

    public void Abandon(ArenaAddress root)
    {
        if (_preparations.Remove(root, out var edits) && edits is not null)
        {
            foreach (var edit in edits.Values)
            {
                _records[edit.Tail].PendingEdit = false;
                if (edit.NewOwner)
                {
                    Forget(edit.Tail);
                }
                else
                {
                    ForgetEmpty(edit.Tail);
                }
            }
        }
    }

    public bool Borrow(RootHandle owner, int field, ArenaAddress lookup, uint offset, uint layoutVersion, int bytes,
        out ArenaAddress slot)
    {
        slot = default;
        var tail = Find(owner, field);
        if (tail < 0)
        {
            return false;
        }

        ref var record = ref _records[tail];
        var full = bytes == Arena.MaxBlockSize - BlockHeader.Size;
        slot = full ? record.Full : record.Last;
        if (slot.IsNull || arena.Payload(slot).Length != bytes)
        {
            return false;
        }

        // Reserve rollback state before changing any native link.
        _borrowed.Add(slot, tail);
        Pop(tail, full);
        record.BorrowedSlots += bytes / sizeof(ulong);
        arena.RewriteColdLookupSlot(slot, lookup, offset, layoutVersion);
        return true;
    }

    public bool ReturnBorrowed(ArenaAddress slot)
    {
        if (!_borrowed.Remove(slot, out var tail))
        {
            return false;
        }

        var slots = arena.Payload(slot).Length / sizeof(ulong);
        arena.RestoreColdLookupSlot(slot);
        _records[tail].BorrowedSlots -= slots;
        Push(tail, slot, slots);
        return true;
    }

    public void PublishBorrowed(ArenaAddress slot)
    {
        if (_borrowed.Remove(slot, out var tail))
        {
            _records[tail].BorrowedSlots -= arena.Payload(slot).Length / sizeof(ulong);
            ReleaseIfUnused(tail);
        }
    }

    // Both callers already crossed the reader epoch. The exact former lookup
    // is still allocated, so its address cannot name a later lookup here.
    public bool Accept(ArenaAddress lookup, ArenaAddress slot)
    {
        var tail = Transferring(lookup);
        if (tail < 0)
        {
            return false;
        }

        var slots = arena.CoolRetiredLookupSlot(lookup, slot) / sizeof(ulong);
        Push(tail, slot, slots);
        Refresh(tail);
        return true;
    }

    public void Finish(ArenaAddress lookup)
    {
        var tail = Transferring(lookup);
        if (tail >= 0)
        {
            _records[tail].Transferring = default;
            ForgetEmpty(tail);
        }
    }

    public void ReleaseOne()
    {
        if (_readyFirst == 0)
        {
            return;
        }

        var tail = _readyFirst - 1;
        RemoveReady(tail);
        AddReady(tail);
        ref var record = ref _records[tail];
        if (record.PendingEdit || record.BorrowedSlots != 0)
        {
            return;
        }

        if (!record.Ended && !record.Shrinking)
        {
            if (time.GetElapsedTime(record.AboveSince) < Underuse)
            {
                return;
            }

            record.Shrinking = true;
        }

        ReleaseSlot(tail, full: !record.Full.IsNull);
    }

    // Under Writes, a failed size estimate can discount ready slots. Only the
    // allocator can decide whether their classes and regions make an allocation fit.
    public bool HasReadyBytes(long bytes)
    {
        foreach (var tail in SpareTails())
        {
            // Each ready slot accounts for its whole native block. Full slots
            // have a common size, and only the last slot can be smaller.
            ref readonly var record = ref _records[tail];
            var blocks = record.ReadySlots / FullSlots + (record.Last.IsNull ? 0 : 1);
            bytes -= record.ReadySlots * (long)sizeof(ulong) + blocks * (long)BlockHeader.Size;
            if (bytes <= 0)
            {
                return true;
            }
        }

        return false;
    }

    // A lease is spare capacity even when the normal shrink threshold is not
    // met. Under Writes, only detached slots can be donated. A borrowed slot
    // is already out of these lists and a retired one has never entered them.
    public bool ReleaseForPressure(int neededClass)
    {
        foreach (var tail in SpareTails())
        {
            ref readonly var record = ref _records[tail];
            if (!record.Full.IsNull)
            {
                ReleaseSlot(tail, full: true);
                return true;
            }

            if (record.Last.IsNull || !arena.CanReleaseColdLookupForPressure(record.Last, neededClass, out var clearRegion))
            {
                continue;
            }

            if (!clearRegion)
            {
                ReleaseSlot(tail, full: false);
            }
            else
            {
                ReleaseRegion(Arena.RegionOf(record.Last));
            }

            return true;
        }

        return false;
    }

    // Adds the record array; the set of roots reports its own.
    public void ReportHeld(List<HeldArray> held)
    {
        if (_records.Length != 0)
        {
            held.Add(new HeldArray(nameof(CollectionTails), typeof(CollectionTail), _records.Length));
        }

        _roots.ReportHeld(held);
    }

    private void ReleaseRegion(long region)
    {
        // Every allocated slot in this region was already checked. Slot
        // donation does not add a lease, and a record released meanwhile
        // is no longer in use, which the walk skips.
        foreach (var tail in SpareTails())
        {
            if (!_records[tail].Last.IsNull && Arena.RegionOf(_records[tail].Last) == region)
            {
                ReleaseSlot(tail, full: false);
            }
        }
    }

    private void ReleaseSlot(int tail, bool full)
    {
        arena.Free(Pop(tail, full));
        Refresh(tail);
        ForgetEmpty(tail);
        ReleaseIfUnused(tail);
    }

    // The tails that hold spare slots: those of the roots that list them, then the ended ones that
    // maintenance still works on. A record released during the walk is skipped.
    private IEnumerable<int> SpareTails()
    {
        for (var tail = 0; tail < _used; tail++)
        {
            if (_records[tail] is { InUse: true, Listed: true })
            {
                yield return tail;
            }
        }

        for (var tail = 0; tail < _used; tail++)
        {
            if (_records[tail] is { InUse: true, Listed: false, Ended: true, Ready: true })
            {
                yield return tail;
            }
        }
    }

    public CollectionSlotOwnership[] Snapshot()
    {
        var result = new List<CollectionSlotOwnership>();
        foreach (var tail in SpareTails())
        {
            AddReady(_records[tail], result);
        }

        foreach (var (slot, tail) in _borrowed)
        {
            ref readonly var record = ref _records[tail];
            result.Add(new(record.Owner, record.Offset, record.Former, slot, Borrowed: true));
        }

        return result.ToArray();
    }

    private void AddReady(in CollectionTail tail, List<CollectionSlotOwnership> result)
    {
        for (var slot = tail.Full; !slot.IsNull; slot = arena.ColdLookupLink(slot))
        {
            result.Add(new(tail.Owner, tail.Offset, tail.Former, slot, Borrowed: false));
        }

        if (!tail.Last.IsNull)
        {
            result.Add(new(tail.Owner, tail.Offset, tail.Former, tail.Last, Borrowed: false));
        }
    }

    private Dictionary<int, CollectionTailEdit> Preparation(ArenaAddress root)
    {
        if (!_preparations.TryGetValue(root, out var changes) || changes is null)
        {
            changes = [];
            _preparations[root] = changes;
        }

        return changes;
    }

    // The record of the field of the root, or -1.
    private int Find(RootHandle handle, int offset)
    {
        if (!_roots.TryGet(new RootTails(handle, 0), out var owner))
        {
            return -1;
        }

        for (var next = owner.First; next != 0; next = _records[next - 1].NextOfRoot)
        {
            if (_records[next - 1].Offset == offset)
            {
                return next - 1;
            }
        }

        return -1;
    }

    // A record for a new tail of the field of the root, listed first among the root's records. The
    // record array and the set of roots grow, charged, before anything changes.
    private int AddTail(RootHandle handle, int offset)
    {
        if (_free == 0 && _used == _records.Length)
        {
            arena.Budget.Grow(ref _records, Math.Max(4, 2 * _records.Length));
        }

        var probe = new RootTails(handle, 0);
        var listed = _roots.TryGet(probe, out var owner);
        if (!listed)
        {
            _roots.EnsureCapacity(_roots.Count + 1);
        }

        int tail;
        if (_free != 0)
        {
            tail = _free - 1;
            _free = _records[tail].NextOfRoot;
        }
        else
        {
            tail = _used++;
        }

        _records[tail] = new CollectionTail
        {
            Owner = handle,
            Offset = offset,
            InUse = true,
            Listed = true,
            NextOfRoot = listed ? owner.First : 0,
        };

        if (listed)
        {
            _roots.Remove(probe);
        }

        _roots.Add(new RootTails(handle, tail + 1));
        return tail;
    }

    private int Transferring(ArenaAddress lookup)
    {
        ref readonly var header = ref arena.Header(lookup);
        var tail = Find(header.Root, (int)header.DescriptorOffset);
        return tail >= 0 && _records[tail].Transferring == lookup ? tail : -1;
    }

    private void Push(int tail, ArenaAddress slot, int slots)
    {
        ref var record = ref _records[tail];
        if (slots == FullSlots)
        {
            arena.SetColdLookupLink(slot, record.Full);
            record.Full = slot;
        }
        else
        {
            // Only the final chunk of one former lookup can use a smaller class.
            record.Last = slot;
        }

        record.ReadySlots += slots;
    }

    private ArenaAddress Pop(int tail, bool full)
    {
        ref var record = ref _records[tail];
        var slot = full ? record.Full : record.Last;
        if (full)
        {
            record.Full = arena.ColdLookupLink(slot);
        }
        else
        {
            record.Last = default;
        }

        record.ReadySlots -= arena.Payload(slot).Length / sizeof(ulong);
        return slot;
    }

    private void End(int tail)
    {
        _records[tail].Ended = true;
        _records[tail].Transferring = default;
        Refresh(tail);
    }

    private void Refresh(int tail)
    {
        ref var record = ref _records[tail];
        if (record.Shrinking && record.Capacity <= 2 * record.Need)
        {
            record.Shrinking = false;
        }

        if (!record.Ended && !record.Shrinking && record.Capacity > 4 * record.Need)
        {
            if (!record.Above)
            {
                record.AboveSince = time.GetTimestamp();
                record.Above = true;
            }
        }
        else if (!record.Shrinking)
        {
            record.Above = false;
        }

        var needed = record.ReadySlots > 0 && (record.Ended || record.Shrinking || record.Above);
        if (needed && !record.Ready)
        {
            AddReady(tail);
        }
        else if (!needed && record.Ready)
        {
            RemoveReady(tail);
        }
    }

    private void ForgetEmpty(int tail)
    {
        // Borrowing itself doesn't change the published capacity. A spare that
        // appears while a write is prepared still belongs to the reserved edit.
        ref readonly var record = ref _records[tail];
        if (record.Empty && !record.Ended && !record.PendingEdit)
        {
            Forget(tail);
        }
    }

    // Takes the record off its root's list, and gives it back when nothing else holds it.
    private void Forget(int tail)
    {
        ref var record = ref _records[tail];
        if (record.Listed)
        {
            var probe = new RootTails(record.Owner, 0);
            _roots.TryGet(probe, out var owner);
            if (owner.First == tail + 1)
            {
                _roots.Remove(probe);
                if (record.NextOfRoot != 0)
                {
                    _roots.Add(new RootTails(record.Owner, record.NextOfRoot));
                }
            }
            else
            {
                var previous = owner.First - 1;
                while (_records[previous].NextOfRoot != tail + 1)
                {
                    previous = _records[previous].NextOfRoot - 1;
                }

                _records[previous].NextOfRoot = record.NextOfRoot;
            }

            record.Listed = false;
            record.NextOfRoot = 0;
        }

        ReleaseIfUnused(tail);
    }

    private void ReleaseIfUnused(int tail)
    {
        ref var record = ref _records[tail];
        if (record is { InUse: true, Listed: false, Ready: false, PendingEdit: false, BorrowedSlots: 0 })
        {
            record = new CollectionTail { NextOfRoot = _free };
            _free = tail + 1;
        }
    }

    private void AddReady(int tail)
    {
        ref var record = ref _records[tail];
        record.Ready = true;
        record.ReadyPrevious = _readyLast;
        record.ReadyNext = 0;
        if (_readyLast == 0)
        {
            _readyFirst = tail + 1;
        }
        else
        {
            _records[_readyLast - 1].ReadyNext = tail + 1;
        }

        _readyLast = tail + 1;
    }

    private void RemoveReady(int tail)
    {
        ref var record = ref _records[tail];
        if (record.ReadyPrevious == 0)
        {
            _readyFirst = record.ReadyNext;
        }
        else
        {
            _records[record.ReadyPrevious - 1].ReadyNext = record.ReadyNext;
        }

        if (record.ReadyNext == 0)
        {
            _readyLast = record.ReadyPrevious;
        }
        else
        {
            _records[record.ReadyNext - 1].ReadyPrevious = record.ReadyPrevious;
        }

        record.Ready = false;
        record.ReadyPrevious = 0;
        record.ReadyNext = 0;
    }

    // A root with tails and its first record plus one; the set compares the root alone.
    private readonly record struct RootTails(RootHandle Handle, int First);

    private sealed class RootComparer : IEqualityComparer<RootTails>
    {
        public bool Equals(RootTails left, RootTails right) => left.Handle.Equals(right.Handle);

        public int GetHashCode(RootTails value) => value.Handle.GetHashCode();
    }
}
