using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Text;

// Text bytes live in native blocks. Protected readers use the stable ID to
// acquire its current address. Writes owns the locator changes, dense counts
// and hash links, and both table generations during bounded migration.
// A root or index node holds a reference until its native retirement. After
// final unlink the ID remains reserved until all its detached blocks go.
internal sealed class TextTable(Arena arena, ulong seed, Lock? writes)
{
    private const int ScratchBytes = 512;
    private const uint Tag = 0x8000_0000;
    private const uint LinkMask = Tag - 1;
    private readonly Arena _arena = arena;
    private readonly ulong _seed = seed;
    private readonly Lock? _writes = writes;
    private readonly TextLocator _locator = TextLocator.Create(arena.Budget);
    private readonly TextWriterSlots _slots = TextWriterSlots.Create(arena.Budget);
    private TextBucketSet _active = TextBucketSet.Create(2, 0, arena.Budget);
    private TextBucketSet? _old;
    private int _bucketCursor;
    private int _deferredWork;

    public static long OwnerBytes => MemoryBudget.ObjectBytes(7L * IntPtr.Size + 2 * sizeof(int));

    public int Count => _slots.Count;

    public long Bytes => OwnerBytes + _locator.Bytes + _slots.Bytes + _active.Bytes + (_old?.Bytes ?? 0);

    public ArenaAddress AddressOf(ulong id) => _locator.Address((uint)id);

    public int Maintain(int workLimit) => Maintain(workLimit, null, deferAllocationFailure: false, releaseWriterPage: true, out _);

    // Optional metadata work during an automatic turn. Charge a refused unit
    // and tell the caller to defer the next attempt to another turn.
    public int MaintainBackground(int workLimit, Action? beforeMigrationPageAllocation, out bool deferred) =>
        Maintain(workLimit, beforeMigrationPageAllocation, deferAllocationFailure: true, releaseWriterPage: true, out deferred);

    private int Maintain(int workLimit, Action? beforeMigrationPageAllocation, bool deferAllocationFailure, bool releaseWriterPage, out bool deferred)
    {
        AssertWrites();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workLimit);
        deferred = false;
        var work = 0;
        for (; work < workLimit; work++)
        {
            try
            {
                if (_old is { } old)
                {
                    Migrate(old, beforeMigrationPageAllocation);
                }
                else if (NeededHeads() is var needed && needed != _active.Capacity)
                {
                    var replacement = TextBucketSet.Create(needed, _active.Tag ^ Tag, _arena.Budget);
                    _old = _active;
                    _active = replacement;
                    _bucketCursor = 0;
                }
                else if (!_locator.TrimOne() && !_slots.TrimOne(releaseWriterPage))
                {
                    break;
                }
            }
            catch (Exception exception) when (deferAllocationFailure && exception is OutOfMemoryException or BudgetRefusedException)
            {
                // Allocating steps reserve replacements before changing the published shape or cursor.
                deferred = true;
                return work + 1;
            }
        }

        return work;
    }

    // A failed preparation must keep the previous bucket layout and locator capacity. Work on
    // existing text can resume after preparation succeeds and its entries are retained.
    public void EndPreparation(bool keeps)
    {
        var work = _deferredWork;
        _deferredWork = 0;
        if (keeps && work != 0)
        {
            Maintain(work, null, deferAllocationFailure: true, releaseWriterPage: false, out _);
        }
    }

    public TextCapacitySnapshot MeasureCapacity()
    {
        AssertWrites();
        var directory = new List<TextAllocationShape>();
        var leaves = new List<TextAllocationShape>();
        _locator.Measure(directory, leaves);
        var slots = new List<TextAllocationShape> { new(1, (int)(OwnerBytes - 2 * IntPtr.Size), 0, 0) };
        _slots.Measure(slots);
        var active = new List<TextAllocationShape>();
        var old = new List<TextAllocationShape>();
        _active.Measure(active);
        _old?.Measure(old);
        return new TextCapacitySnapshot(directory, leaves, slots, active, old, []);
    }

    public bool TryMeasureEntry(uint id, out TextEntrySnapshot entry)
    {
        AssertWrites();
        var ordinal = _locator.Ordinal(id);
        if (ordinal < 0)
        {
            entry = default;
            return false;
        }

        ref var slot = ref _slots.At((uint)ordinal + 1);
        var table = TableOf(slot.Next);
        var depth = 0;
        for (var previous = slot.Previous; previous != 0; previous = _slots.At(previous).Previous)
        {
            depth++;
        }

        entry = new TextEntrySnapshot(id, AddressOf(id), ordinal, ReferenceEquals(table, _active) ? TextBucketSetEnum.Active : TextBucketSetEnum.Old,
            table.Bucket(slot.Hash), depth, slot.References);
        return true;
    }

    // Under Writes, observes the actual page identity without changing its lifetime in the table.
    public object? CaptureWriterPage(uint page)
    {
        AssertWrites();
        return _slots.CapturePage(page);
    }

    public bool TryCaptureLocatorLeaf(uint id, out IReadOnlyList<TextLocatorAllocation> actual)
    {
        AssertWrites();
        return _locator.TryCapture(id, out actual);
    }

    public bool TryAcquire(ReadOnlySpan<char> text, LatticeStringEncodingEnum encoding, int length, out ValueDescriptor descriptor, out long newBytes)
    {
        Span<byte> scratch = stackalloc byte[ScratchBytes];
        var pieces = TextPieces.OfText(text, encoding, length, scratch);
        return TryAcquire(ref pieces, length, out descriptor, out newBytes);
    }

    public bool TryAcquire(ReadOnlySequence<byte> bytes, out ValueDescriptor descriptor, out long newBytes)
    {
        var pieces = TextPieces.OfReceived(bytes);
        return TryAcquire(ref pieces, (int)bytes.Length, out descriptor, out newBytes);
    }

    public void AddReference(ulong id)
    {
        AssertWrites();
        _slots.At(ReferenceOf((uint)id)).References++;
    }

    public void Release(ulong id)
    {
        AssertWrites();
        var number = (uint)id;
        if (!TryDetachLastReference(number, out var address, out var length))
        {
            return;
        }

        ValueBlocks.FreeText(_arena, address, length);
        _locator.Return(number);
    }

    public bool ReleaseStep(ref TextReclamation work)
    {
        AssertWrites();
        if (!work.Detached)
        {
            if (!TryDetachLastReference(work.Id, out work.Address, out var length))
            {
                work = default;
                return true;
            }

            work.ChunkCount = length > ValueBlocks.SingleBlockLimit ? ValueBlocks.ChunkCount((int)length) : 0;
            work.Detached = true;
            return false;
        }

        if (!work.PendingChunk.IsNull)
        {
            _arena.Free(work.PendingChunk);
            work.PendingChunk = default;
            return false;
        }

        if (work.NextChunk < work.ChunkCount)
        {
            work.PendingChunk = ValueBlocks.ChunkAddressAt(_arena, work.Address, work.NextChunk++);
            return false;
        }

        if (!work.Address.IsNull)
        {
            _arena.Free(work.Address);
            work.Address = default;
            return false;
        }

        _locator.Return(work.Id);
        work = default;
        return true;
    }

    public void Publish(ulong id)
    {
        var address = AddressOf(id);
        if (_arena.IsPrepared(address))
        {
            ValueBlocks.PublishText(_arena, address, _slots.At(ReferenceOf((uint)id)).Length);
        }
    }

    public List<(byte[] Text, long References)> References()
    {
        var references = new List<(byte[] Text, long References)>(Count);
        for (var reference = 1u; reference <= (uint)Count; reference++)
        {
            ref var slot = ref _slots.At(reference);
            var descriptor = Physical(slot);
            var bytes = new byte[slot.Length];
            ValueBlocks.CopyTo(_arena, descriptor, default, bytes);
            references.Add((bytes, slot.References));
        }

        return references;
    }

    public bool TryLocation(uint id, out ArenaAddress address, out uint length)
    {
        AssertWrites();
        var ordinal = _locator.Ordinal(id);
        if (ordinal < 0)
        {
            address = default;
            length = 0;
            return false;
        }

        address = AddressOf(id);
        length = _slots.At((uint)ordinal + 1).Length;
        return true;
    }

    public void MoveAddress(uint id, ArenaAddress to)
    {
        AssertWrites();
        _locator.SetAddress(id, to);
    }

    private bool TryAcquire(ref TextPieces pieces, int length, out ValueDescriptor descriptor, out long newBytes)
    {
        AssertWrites();
        var hash = HashOf(ref pieces);
        var found = Find(_active, hash, length, ref pieces);
        if (found == 0 && _old is { } old)
        {
            found = Find(old, hash, length, ref pieces);
        }

        if (found != 0)
        {
            ref var slot = ref _slots.At(found);
            slot.References++;
            descriptor = Resident(slot.Id, slot.Length);
            newBytes = 0;
            return true;
        }

        return TryAdd(ref pieces, hash, length, out descriptor, out newBytes);
    }

    private uint Find(TextBucketSet table, uint hash, int length, ref TextPieces pieces)
    {
        for (var reference = table.Head(table.Bucket(hash)); reference != 0; reference = _slots.At(reference).Next & LinkMask)
        {
            ref var slot = ref _slots.At(reference);
            if (slot.Hash == hash && slot.Length == (uint)length && HoldsBytes(slot, ref pieces))
            {
                return reference;
            }
        }

        return 0;
    }

    private bool TryAdd(ref TextPieces pieces, uint hash, int length, out ValueDescriptor descriptor, out long newBytes)
    {
        descriptor = default;
        newBytes = 0;

        // A miss can do bounded work when growth cannot keep pace. An
        // existing text never enters this path or advances a migration.
        if (_old is not null || Count >= 4L * _active.Capacity)
        {
            // Catch up on placement without returning the empty page this
            // write can reuse. The automatic turn can return it later.
            if (_arena.Budget is { IsPreparing: true })
            {
                _deferredWork += 2;
            }
            else
            {
                Maintain(2, null, deferAllocationFailure: false, releaseWriterPage: false, out _);
            }
        }

        if (!_locator.TryTake(out var id))
        {
            return false;
        }

        var bucket = _active.Bucket(hash);
        var added = false;
        try
        {
            _slots.Reserve();
            _active.Reserve(bucket);
            var source = pieces.Source();
            if (!ValueBlocks.TryStoreText(_arena, id, length, ref source, out var physical))
            {
                return false;
            }

            var reference = _slots.Add(new TextSlot { Id = id, Hash = hash, Length = (uint)length, References = 1 });
            _locator.SetOrdinal(id, (int)reference - 1);
            _locator.SetAddress(id, ValueBlocks.AddressOf(physical));
            Link(_active, bucket, reference);
            added = true;
            descriptor = Resident(id, (uint)length);
            newBytes = ValueBlocks.SlotBytesOf(length);
            return true;
        }
        finally
        {
            if (!added)
            {
                _active.ReleaseReservation(bucket);
                _slots.ReleaseReservation();
                _locator.Return(id);
            }
        }
    }

    private void Migrate(TextBucketSet old, Action? beforePageAllocation)
    {
        if (!old.TryPage(_bucketCursor, out var bucket))
        {
            _old = null;
            old.ReleaseBacking();
            return;
        }

        var reference = old.Head(bucket);
        if (reference == 0)
        {
            _bucketCursor = bucket + 1;
            return;
        }

        var destination = _active.Bucket(_slots.At(reference).Hash);
        _active.Reserve(destination, beforePageAllocation);
        Unlink(old, reference);
        Link(_active, destination, reference);
        _bucketCursor = bucket;
    }

    private void Link(TextBucketSet table, int bucket, uint reference)
    {
        var first = table.Head(bucket);
        ref var slot = ref _slots.At(reference);
        slot.Previous = 0;
        slot.Next = table.Tag | first;
        if (first != 0)
        {
            _slots.At(first).Previous = reference;
        }

        table.SetHead(bucket, reference);
    }

    private void Unlink(TextBucketSet table, uint reference)
    {
        ref var slot = ref _slots.At(reference);
        var next = slot.Next & LinkMask;
        if (slot.Previous == 0)
        {
            table.SetHead(table.Bucket(slot.Hash), next);
        }
        else
        {
            ref var previous = ref _slots.At(slot.Previous);
            previous.Next = table.Tag | next;
        }

        if (next != 0)
        {
            _slots.At(next).Previous = slot.Previous;
        }
    }

    // Unlinking leaves the ID reserved until its caller finishes freeing the native blocks.
    private bool TryDetachLastReference(uint id, out ArenaAddress address, out uint length)
    {
        var reference = ReferenceOf(id);
        ref var slot = ref _slots.At(reference);
        if (--slot.References != 0)
        {
            address = default;
            length = 0;
            return false;
        }

        length = slot.Length;
        address = AddressOf(id);
        Remove(reference);
        return true;
    }

    private void Remove(uint reference)
    {
        ref var slot = ref _slots.At(reference);
        Unlink(TableOf(slot.Next), reference);
        _locator.SetOrdinal(slot.Id, -1);

        var last = (uint)Count;
        if (reference != last)
        {
            slot = _slots.At(last);
            var table = TableOf(slot.Next);
            if (slot.Previous == 0)
            {
                table.SetHead(table.Bucket(slot.Hash), reference);
            }
            else
            {
                _slots.At(slot.Previous).Next = table.Tag | reference;
            }

            var next = slot.Next & LinkMask;
            if (next != 0)
            {
                _slots.At(next).Previous = reference;
            }

            _locator.SetOrdinal(slot.Id, (int)reference - 1);
        }

        _slots.Pop();
    }

    private int NeededHeads() => (int)BitOperations.RoundUpToPowerOf2(Math.Max(2u, ((uint)Count + 3) / 4));

    private uint ReferenceOf(uint id) => (uint)(_locator.Ordinal(id) + 1);

    private TextBucketSet TableOf(uint next) => (next & Tag) == _active.Tag ? _active : _old!;

    private static ValueDescriptor Resident(uint id, uint length) => new() { Kind = DescriptorKindEnum.Text, Length = length, Location = id };

    private ValueDescriptor Physical(in TextSlot slot) =>
        new() { Kind = DescriptorKindEnum.Text, Length = slot.Length, Location = AddressOf(slot.Id).Value };

    private uint HashOf(ref TextPieces pieces)
    {
        var hash = new Marvin(_seed);
        pieces.Restart();
        for (var piece = pieces.Next(); !piece.IsEmpty; piece = pieces.Next())
        {
            hash.Append(piece);
        }

        return hash.Finish();
    }

    private bool HoldsBytes(in TextSlot slot, ref TextPieces pieces)
    {
        var descriptor = Physical(slot);
        var chunked = ValueBlocks.LiesInChunks(descriptor);
        var entries = chunked ? ValueBlocks.Directory(_arena, descriptor) : default;
        var stored = chunked ? ValueBlocks.Chunk(_arena, entries, 0) : ValueBlocks.Contiguous(_arena, descriptor, default);
        var nextChunk = 1;
        pieces.Restart();
        for (var piece = pieces.Next(); !piece.IsEmpty; piece = pieces.Next())
        {
            while (!piece.IsEmpty)
            {
                if (stored.IsEmpty)
                {
                    stored = ValueBlocks.Chunk(_arena, entries, nextChunk++);
                }

                var count = Math.Min(piece.Length, stored.Length);
                if (!piece[..count].SequenceEqual(stored[..count]))
                {
                    return false;
                }

                piece = piece[count..];
                stored = stored[count..];
            }
        }

        return true;
    }

    private void AssertWrites() => Debug.Assert(_writes is null || _writes.IsHeldByCurrentThread);
}
