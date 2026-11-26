namespace Moltaro.Lattice.Storage;

// The writer's spare lookup slots (Q17, allocation-and-compaction.md). A slot
// is reachable from its old immutable lookup until the safe FIFO walk cools it.
// Ready slots stay allocated in the arena. A preparation unlinks a slot, and
// either publishes it as active or puts the same grant back on refusal.
//
// Storage for an owner and its worker link is provided during write
// preparation. One maintenance call considers one owner and gives back at
// most one slot. The lease never belongs to a published descriptor and all
// changes to membership, age and native allocation hold Writes.
internal sealed class CollectionTails(Arena arena, TimeProvider time)
{
    public const int FullSlots = (Arena.MaxBlockSize - BlockHeader.Size) / sizeof(ulong);

    private static readonly TimeSpan Underuse = TimeSpan.FromSeconds(5);

    private readonly Dictionary<RootHandle, Dictionary<int, CollectionTail>> _owners = [];
    private readonly Dictionary<ArenaAddress, Dictionary<int, CollectionTailEdit>> _preparations = [];
    private readonly Dictionary<ArenaAddress, CollectionTail> _borrowed = [];
    private readonly LinkedList<CollectionTail> _ready = new();

    public bool HasOwner(RootHandle handle) => _owners.ContainsKey(handle);

    public bool HasSlots(RootHandle handle, int offset) => Find(handle, offset) is { ReadySlots: > 0 };

    public void PrepareSparse(ArenaAddress root) => _preparations.Add(root, []);

    public void PrepareChange(ArenaAddress root, RootHandle handle, int offset, ArenaAddress former, int active, int need)
    {
        var tail = Find(handle, offset);
        if (tail is null && former.IsNull)
        {
            return;
        }

        var isNew = tail is null;
        if (tail is null)
        {
            tail = new CollectionTail(handle, offset);
            tail.Work = new LinkedListNode<CollectionTail>(tail);
        }

        Preparation(root).Add(offset, new(tail, former, active, need, isNew, Ended: false));
        tail.PendingEdit = true;
        if (isNew)
        {
            if (_owners.TryGetValue(handle, out var fields))
            {
                fields.Add(offset, tail);
            }
            else
            {
                _owners.Add(handle, new Dictionary<int, CollectionTail> { [offset] = tail });
            }
        }
    }

    public void PrepareEnd(ArenaAddress root, RootHandle handle, int offset)
    {
        if (Find(handle, offset) is { } tail)
        {
            Preparation(root).TryAdd(offset, new(tail, default, 0, 0, NewOwner: false, Ended: true));
            tail.PendingEdit = true;
        }
    }

    public void Apply(RootHandle handle, ArenaAddress root)
    {
        if (!_preparations.Remove(root, out var edits))
        {
            // A complete replacement or deletion ends every lease of this generation.
            if (_owners.Remove(handle, out var fields))
            {
                foreach (var tail in fields.Values)
                {
                    End(tail);
                }
            }

            return;
        }

        foreach (var edit in edits.Values)
        {
            var tail = edit.Tail;
            tail.PendingEdit = false;
            if (edit.Ended)
            {
                Forget(tail);
                End(tail);
                continue;
            }

            tail.Active = edit.Active;
            tail.Need = edit.Need;
            if (!edit.Former.IsNull && tail.Empty)
            {
                tail.Former = edit.Former;
                tail.Transferring = edit.Former;
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
        if (_preparations.Remove(root, out var edits))
        {
            foreach (var edit in edits.Values)
            {
                edit.Tail.PendingEdit = false;
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
        if (Find(owner, field) is not { } tail)
        {
            return false;
        }

        var full = bytes == Arena.MaxBlockSize - BlockHeader.Size;
        slot = full ? tail.Full : tail.Last;
        if (slot.IsNull || arena.Payload(slot).Length != bytes)
        {
            return false;
        }

        // Reserve rollback state before changing any native link.
        _borrowed.Add(slot, tail);
        Pop(tail, full);
        tail.BorrowedSlots += bytes / sizeof(ulong);
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
        tail.BorrowedSlots -= slots;
        Push(tail, slot, slots);
        return true;
    }

    public void PublishBorrowed(ArenaAddress slot)
    {
        if (_borrowed.Remove(slot, out var tail))
        {
            tail.BorrowedSlots -= arena.Payload(slot).Length / sizeof(ulong);
        }
    }

    // Both callers already crossed the reader epoch. The exact former lookup
    // is still allocated, so its address cannot name a later lookup here.
    public bool Accept(ArenaAddress lookup, ArenaAddress slot)
    {
        if (Transferring(lookup) is not { } tail)
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
        if (Transferring(lookup) is { } tail)
        {
            tail.Transferring = default;
            ForgetEmpty(tail);
        }
    }

    public void ReleaseOne()
    {
        if (_ready.First is not { } work)
        {
            return;
        }

        _ready.RemoveFirst();
        _ready.AddLast(work);
        var tail = work.Value;
        if (tail.PendingEdit || tail.BorrowedSlots != 0)
        {
            return;
        }

        if (!tail.Ended && !tail.Shrinking)
        {
            if (time.GetElapsedTime(tail.AboveSince!.Value) < Underuse)
            {
                return;
            }

            tail.Shrinking = true;
        }

        ReleaseSlot(tail, full: !tail.Full.IsNull);
    }

    // Under Writes, a failed size estimate can discount ready slots. Only the
    // allocator can decide whether their classes and regions make an allocation fit.
    public bool HasReadyBytes(long bytes)
    {
        foreach (var tail in SpareTails())
        {
            // Each ready slot accounts for its whole native block. Full slots
            // have a common size, and only the last slot can be smaller.
            var blocks = tail.ReadySlots / FullSlots + (tail.Last.IsNull ? 0 : 1);
            bytes -= tail.ReadySlots * (long)sizeof(ulong) + blocks * (long)BlockHeader.Size;
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
            if (!tail.Full.IsNull)
            {
                ReleaseSlot(tail, full: true);
                return true;
            }

            if (tail.Last.IsNull || !arena.CanReleaseColdLookupForPressure(tail.Last, neededClass, out var clearRegion))
            {
                continue;
            }

            if (!clearRegion)
            {
                ReleaseSlot(tail, full: false);
            }
            else
            {
                ReleaseRegion(Arena.RegionOf(tail.Last));
            }

            return true;
        }

        return false;
    }

    private void ReleaseRegion(long region)
    {
        // Every allocated slot in this region was already checked. Slot
        // donation does not add a lease, and Dictionary permits removal
        // of empty owners and fields while enumerating them.
        foreach (var tail in SpareTails())
        {
            if (!tail.Last.IsNull && Arena.RegionOf(tail.Last) == region)
            {
                ReleaseSlot(tail, full: false);
            }
        }
    }

    private void ReleaseSlot(CollectionTail tail, bool full)
    {
        arena.Free(Pop(tail, full));
        Refresh(tail);
        ForgetEmpty(tail);
    }

    private IEnumerable<CollectionTail> SpareTails()
    {
        foreach (var fields in _owners.Values)
        {
            foreach (var tail in fields.Values)
            {
                yield return tail;
            }
        }

        // An ended owner is removed from _owners but keeps its ready worker
        // link until its last slot is returned. Its link may go during yield.
        var work = _ready.First;
        while (work is not null)
        {
            var tail = work.Value;
            work = work.Next;
            if (tail.Ended)
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
            AddReady(tail, result);
        }

        foreach (var (slot, tail) in _borrowed)
        {
            result.Add(new(tail.Owner, tail.Offset, tail.Former, slot, Borrowed: true));
        }

        return result.ToArray();
    }

    private void AddReady(CollectionTail tail, List<CollectionSlotOwnership> result)
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
        if (!_preparations.TryGetValue(root, out var changes))
        {
            changes = [];
            _preparations.Add(root, changes);
        }

        return changes;
    }

    private CollectionTail? Find(RootHandle handle, int offset) =>
        _owners.TryGetValue(handle, out var fields) && fields.TryGetValue(offset, out var tail) ? tail : null;

    private CollectionTail? Transferring(ArenaAddress lookup)
    {
        ref readonly var header = ref arena.Header(lookup);
        var tail = Find(header.Root, (int)header.DescriptorOffset);
        return tail is not null && tail.Transferring == lookup ? tail : null;
    }

    private void Push(CollectionTail tail, ArenaAddress slot, int slots)
    {
        if (slots == FullSlots)
        {
            arena.SetColdLookupLink(slot, tail.Full);
            tail.Full = slot;
        }
        else
        {
            // Only the final chunk of one former lookup can use a smaller class.
            tail.Last = slot;
        }

        tail.ReadySlots += slots;
    }

    private ArenaAddress Pop(CollectionTail tail, bool full)
    {
        var slot = full ? tail.Full : tail.Last;
        if (full)
        {
            tail.Full = arena.ColdLookupLink(slot);
        }
        else
        {
            tail.Last = default;
        }

        tail.ReadySlots -= arena.Payload(slot).Length / sizeof(ulong);
        return slot;
    }

    private void End(CollectionTail tail)
    {
        tail.Ended = true;
        tail.Transferring = default;
        Refresh(tail);
    }

    private void Refresh(CollectionTail tail)
    {
        if (tail.Shrinking && tail.Capacity <= 2 * tail.Need)
        {
            tail.Shrinking = false;
        }

        if (!tail.Ended && !tail.Shrinking && tail.Capacity > 4 * tail.Need)
        {
            tail.AboveSince ??= time.GetTimestamp();
        }
        else if (!tail.Shrinking)
        {
            tail.AboveSince = null;
        }

        var work = tail.Work!;
        var needed = tail.ReadySlots > 0 && (tail.Ended || tail.Shrinking || tail.AboveSince.HasValue);
        if (needed && work.List is null)
        {
            _ready.AddLast(work);
        }
        else if (!needed && work.List is not null)
        {
            _ready.Remove(work);
        }
    }

    private void ForgetEmpty(CollectionTail tail)
    {
        // Borrowing itself doesn't change the published capacity. A spare that
        // appears while a write is prepared still belongs to the reserved edit.
        if (tail.Empty && !tail.Ended && !tail.PendingEdit)
        {
            Forget(tail);
        }
    }

    private void Forget(CollectionTail tail)
    {
        if (_owners.TryGetValue(tail.Owner, out var fields) &&
            fields.TryGetValue(tail.Offset, out var current) && current == tail)
        {
            fields.Remove(tail.Offset);
            if (fields.Count == 0)
            {
                _owners.Remove(tail.Owner);
            }
        }
    }
}
