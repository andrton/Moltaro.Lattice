using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Text;

namespace Moltaro.Lattice.Storage;

// The native memory of one database (milestone contract, "Native layout and allocation"; Q17,
// "Size classes and reusable slots" and "Empty-segment retention and release"). The arena
// obtains segments from the native allocator and divides each into regions of
// ArenaSegment.RegionSize bytes. Each region serves one size class and tracks its free slots, and
// the regions of a class that have a free slot are kept in a list, so that an allocation finds
// space without scanning the arena. Every block starts with a BlockHeader and is reached by its
// relative address, which the arena checks against its own metadata before it reads anything
// through it.
//
// The arena owns every segment it holds, in use or reserved. It obtains their native memory through
// its NativeAllocator and returns each segment's memory there exactly once, in one of two places:
// in Free, when a segment is left empty and the reserve has no room for it, and in Dispose, for
// everything still held. Whoever owns the arena disposes it; from S06 on that is the database, when
// it closes and no read is in progress any more. The arena has no finalizer, so an arena that is
// never disposed keeps its native memory until the process ends.
//
// The states of a block. A block is free, prepared, live or retired. TryAllocatePrepared gives a
// prepared block, which no reader can reach, taken for a write that has not published it yet or held in
// the standing reserve of an index for no write, and Publish makes it live when a write publishes it. Retire detaches a live
// block from its owner while reads may still reach it: a write that replaced or deleted its root
// retires the blocks of the old root, and frees them once the reads that could reach them have left
// (arena protocol, "Deferred reclamation, 16 November 2026"). Every kind is valid from its
// allocation until Free (brief of S06, the planner's answer to Q30). A block is counted in exactly
// one place: a prepared block with its whole slot in PreparedBytes, a live one with its header and
// payload in LiveBytes and the rest of its slot in ClassSlackBytes, a retired one with its whole
// slot in RetiredBytes.
//
// Who uses the arena at the same time. The arena takes no lock. One thread at a time changes it, by
// TryAllocatePrepared, Publish, Retire and Free: in the database that is the thread
// that holds the lock of the writes (Engine.Writes.WriteCoordinator), which also prepares every write
// under that lock. Reads inside the protection of reads (Engine.Protection) call Header and Payload at
// the same time, for the blocks of the roots they reached, which are live or retired and
// are not freed while such a read is inside. Those read only the metadata of the block's own slot,
// region and segment, which no change of the arena alters while the block is allocated; a change for
// another block writes other bits of the same words and other elements of the same arrays, and a
// reader sees their old or their new value, either of which leaves the bit of its own block set. A
// grown segment table is a new array: a reader that still has the old one finds the same segments in
// it. A reader learns of a block only through the publication of a root, which is ordered after
// everything the arena wrote for that block (Engine.Entities.ChangePublisher says which store
// publishes).
internal sealed unsafe class Arena : IDisposable
{
    // The size of the first segment table: slot 0, which is never used, and three segments. The
    // table doubles when it is full.
    private const int FirstSegmentTableLength = 4;

    // The region number that names no region; see RegionNumber.
    private const long NoRegion = 0;

    private readonly int _segmentSize;

    private readonly int _regionsPerSegment;

    private readonly long _memoryLimit;

    private readonly int _reserveLimit;

    private readonly NativeAllocator _allocator;

    // The segments by their slot, which is the segment of an ArenaAddress; null where no segment
    // is held. Slot 0 is never used, so that no block has the address zero, which is null.
    private ArenaSegment?[] _segments = new ArenaSegment?[FirstSegmentTableLength];

    // Slots of the table whose segment was freed, to be given to the next new segment. Its capacity
    // is kept at the length of the table, so that Free never allocates to push a slot.
    private readonly Stack<int> _freedSegmentSlots = new(FirstSegmentTableLength);

    // The slots of the reserved segments: completely empty, held for reuse. Its capacity is kept at
    // the length of the table, for the same reason.
    private readonly Stack<int> _reservedSegments = new(FirstSegmentTableLength);

    // For each size class, the first of the regions that serve it and have a free slot. The lists
    // are linked through ArenaRegion.Previous and Next, so they take no memory of their own.
    private readonly long[] _regionsWithFreeSlot = new long[SizeClasses.Count];

    // The counters, the first of the free regions (the regions of the segments in use that serve no
    // class, linked in the same way), how many free regions and segments there are, the reserved ones
    // included, and the lowest slot of the table never given to a segment: what allocations and frees
    // change, on lines of their own (ArenaTally).
    private ArenaTally _tally = new() { NextUnusedSegmentSlot = 1 };

    private bool _disposed;

    // Takes the settings; segments are obtained on demand. Refuses null options with
    // ArgumentNullException, and with ArgumentOutOfRangeException a MemoryLimit smaller than one
    // segment, a SegmentSize smaller than MaxBlockSize or not a whole multiple of it, and a
    // negative ReservedSegments.
    public Arena(ArenaOptions options)
        : this(options, new NativeAllocator())
    {
    }

    // The same, with the allocator through which the arena obtains and returns the native memory of
    // its segments. Only a test of the engine passes another than NativeAllocator itself (exception
    // X3). The text table hashes with the seed 0 and knows no lock of the writes: an arena a test of the
    // engine makes by itself has no database around it.
    internal Arena(ArenaOptions options, NativeAllocator allocator)
        : this(options, allocator, textSeed: 0, writes: null)
    {
    }

    // The arena of a database: its text table hashes with the seed of the instance and, in a Debug
    // build, asserts at every change of a count that the thread holds the lock of the writes given.
    internal Arena(ArenaOptions options, NativeAllocator allocator, ulong textSeed, Lock? writes)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.SegmentSize, MaxBlockSize);

        if (options.SegmentSize % ArenaSegment.RegionSize != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ArenaOptions.SegmentSize), options.SegmentSize, $"The segment size has to be a whole multiple of {ArenaSegment.RegionSize} bytes.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.MemoryLimit, (long)options.SegmentSize);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ReservedSegments);

        _segmentSize = options.SegmentSize;
        _regionsPerSegment = options.SegmentSize / ArenaSegment.RegionSize;
        _memoryLimit = options.MemoryLimit;
        _reserveLimit = options.ReservedSegments;
        _allocator = allocator;
        Texts = new TextTable(this, textSeed, writes);
    }

    // The text entries of the database, in this arena (see the class).
    public TextTable Texts { get; }

    // The size of one segment in bytes, as the options gave it.
    public int SegmentSize => _segmentSize;

    // The most native capacity the arena may obtain, in bytes, as the options gave it
    // (ArenaOptions.MemoryLimit).
    public long MemoryLimit => _memoryLimit;

    // The largest size class, header included. The largest payload is therefore MaxBlockSize
    // minus the 32-byte header, 65,504 bytes: the chunk payload of the milestone contract.
    public const int MaxBlockSize = 64 * 1024;

    // The size class of a block with this payload: the smallest class that holds the 32-byte
    // header and payloadLength bytes. The classes are those of the milestone contract: 16-byte
    // steps up to 1 KiB, 256-byte steps up to 4 KiB, then powers of two up to MaxBlockSize. A
    // payload of 0 is allowed; its block is 32 bytes, the smallest class. A negative length, or
    // one that does not fit MaxBlockSize with the header, is refused with
    // ArgumentOutOfRangeException.
    public static int BlockSizeFor(int payloadLength) =>
        SizeClasses.SizeOf(SizeClasses.IndexFor(payloadLength));

    // Allocates a block for payloadLength bytes of payload, in the size class BlockSizeFor gives.
    // It takes a free slot of that class, then a free region, then a reserved segment, and only
    // then a new segment.
    //
    // It writes the whole header, also in a reused slot: the root, the kind and the payload length
    // as given, every other field zero. It does not check the kind or the root. The payload is not
    // initialized: the caller writes every byte it later reads.
    //
    // Returns false, with the counters unchanged, when the allocation would take Segments times
    // SegmentSize beyond ArenaOptions.MemoryLimit; that is the only case of false. A length that
    // BlockSizeFor refuses is refused in the same way, with ArgumentOutOfRangeException. When the
    // native allocator cannot supply a new segment, its OutOfMemoryException leaves the arena as
    // it was.
    //
    // The block is prepared: no reader can reach it, its whole slot counts in PreparedBytes, and nothing of it
    // in LiveBytes or ClassSlackBytes, until Publish makes it live.
    public bool TryAllocatePrepared(int payloadLength, BlockKindEnum kind, RootHandle root, out ArenaAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sizeClass = SizeClasses.IndexFor(payloadLength);

        if (_regionsWithFreeSlot[sizeClass] == NoRegion && !TryTakeRegion(sizeClass))
        {
            address = default;

            return false;
        }

        var regionNumber = _regionsWithFreeSlot[sizeClass];
        var segmentSlot = SegmentSlotOf(regionNumber);
        var regionIndex = RegionIndexOf(regionNumber);
        var segment = _segments[segmentSlot]!;
        var blockSize = SizeClasses.SizeOf(sizeClass);
        var slotCount = ArenaSegment.RegionSize / blockSize;

        var slot = segment.TakeSlot(regionIndex, slotCount);

        if (segment.Regions[regionIndex].Allocated == slotCount)
        {
            Unlink(ref _regionsWithFreeSlot[sizeClass], regionNumber);
        }

        var offset = regionIndex * ArenaSegment.RegionSize + slot * blockSize;

        *(BlockHeader*)(segment.Memory + offset) = new BlockHeader
        {
            Root = root,
            PayloadLength = (uint)payloadLength,
            Kind = kind,
        };

        segment.SetPrepared(regionIndex, slot, prepared: true);
        _tally.PreparedBytes += blockSize;
        address = new ArenaAddress((uint)segmentSlot, (uint)offset);

        return true;
    }

    // Makes a prepared block live, when a write publishes it: its slot leaves
    // PreparedBytes, its header and the payload length its header gives join LiveBytes, and the rest
    // of the slot ClassSlackBytes. It changes no byte of the block and allocates nothing, so it
    // cannot fail for a block that is prepared. An invalid address, or that of a block already freed,
    // is refused with ArgumentException, and a block that is not prepared with
    // InvalidOperationException, as is a header whose length exceeds the slot; then nothing changes.
    public void Publish(ArenaAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var segment = Locate(address, out var regionIndex, out var slot) ?? throw NotABlock(address);

        if (!segment.IsPrepared(regionIndex, slot))
        {
            throw new InvalidOperationException($"The block at {address} is not prepared; only a prepared block is published.");
        }

        var blockSize = SizeClasses.SizeOf(segment.Regions[regionIndex].SizeClass);
        var payloadLength = ((BlockHeader*)(segment.Memory + address.Offset))->PayloadLength;

        if (payloadLength > blockSize - BlockHeader.Size)
        {
            throw LengthBeyondSlot(payloadLength, blockSize);
        }

        segment.SetPrepared(regionIndex, slot, prepared: false);

        _tally.PreparedBytes -= blockSize;
        _tally.LiveBytes += BlockHeader.Size + payloadLength;
        _tally.ClassSlackBytes += blockSize - BlockHeader.Size - payloadLength;
    }

    // Retires a live block, when the write that replaced or deleted its owner is published: the block
    // stays allocated and valid, and its bytes stay as they are, so a read that reached it before
    // goes on reading it, but it belongs to no owner any more. Its header and payload length leave
    // LiveBytes and the rest of its slot ClassSlackBytes, and its whole slot joins RetiredBytes, until
    // Free. It changes no byte of the block and allocates nothing, so it cannot fail for a live block.
    // An invalid address, or that of a block already freed, is refused with ArgumentException; a block
    // that is prepared or already retired with InvalidOperationException, as is a header whose length
    // exceeds the slot; then nothing changes.
    public void Retire(ArenaAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var segment = Locate(address, out var regionIndex, out var slot) ?? throw NotABlock(address);

        if (segment.IsPrepared(regionIndex, slot) || segment.IsRetired(regionIndex, slot))
        {
            throw new InvalidOperationException($"The block at {address} is not live; only a live block is retired.");
        }

        var blockSize = SizeClasses.SizeOf(segment.Regions[regionIndex].SizeClass);
        var payloadLength = ((BlockHeader*)(segment.Memory + address.Offset))->PayloadLength;

        if (payloadLength > blockSize - BlockHeader.Size)
        {
            throw LengthBeyondSlot(payloadLength, blockSize);
        }

        segment.SetRetired(regionIndex, slot, retired: true);

        _tally.LiveBytes -= BlockHeader.Size + payloadLength;
        _tally.ClassSlackBytes -= blockSize - BlockHeader.Size - payloadLength;
        _tally.RetiredBytes += blockSize;
    }

    // Whether the address is that of a prepared block: valid, and not published since its
    // allocation. Nothing is read through the address to decide this.
    public bool IsPrepared(ArenaAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var segment = Locate(address, out var regionIndex, out var slot);

        return segment is not null && segment.IsPrepared(regionIndex, slot);
    }

    // Returns the block's slot to its region, where the next allocation of its class can reuse it.
    // A region left without blocks returns to its segment. A segment left empty enters the reserve
    // when the reserve has room, and is otherwise freed at once, inside this call; the background
    // release of Q17 comes with S23. An invalid address, or that of a block already freed, is
    // refused with ArgumentException before anything is read through it.
    //
    // A live block makes LiveBytes and ClassSlackBytes lose what its header says, because the arena
    // keeps no length of its own. A header whose length exceeds the capacity of the slot is refused
    // with InvalidOperationException, as Payload refuses it, and the block stays allocated. A length
    // within the slot that differs from the one given at allocation shifts bytes between the two
    // counters, and their sum stays right; a caller does not write the length through the header,
    // and S20 brings the member that changes it. A prepared block, one of a write that was
    // abandoned, makes PreparedBytes lose its whole slot, and a retired block RetiredBytes.
    public void Free(ArenaAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var segment = Locate(address, out var regionIndex, out var slot) ?? throw NotABlock(address);
        ref var region = ref segment.Regions[regionIndex];
        var sizeClass = region.SizeClass;
        var blockSize = SizeClasses.SizeOf(sizeClass);
        var payloadLength = ((BlockHeader*)(segment.Memory + address.Offset))->PayloadLength;
        var prepared = segment.IsPrepared(regionIndex, slot);
        var retired = segment.IsRetired(regionIndex, slot);

        if (!prepared && !retired && payloadLength > blockSize - BlockHeader.Size)
        {
            throw LengthBeyondSlot(payloadLength, blockSize);
        }

        var slotCount = ArenaSegment.RegionSize / blockSize;
        var wasFull = region.Allocated == slotCount;
        var regionNumber = RegionNumber((int)address.Segment, regionIndex);

        segment.ReturnSlot(regionIndex, slot);

        if (prepared)
        {
            segment.SetPrepared(regionIndex, slot, prepared: false);
            _tally.PreparedBytes -= blockSize;
        }
        else if (retired)
        {
            segment.SetRetired(regionIndex, slot, retired: false);
            _tally.RetiredBytes -= blockSize;
        }
        else
        {
            _tally.LiveBytes -= BlockHeader.Size + payloadLength;
            _tally.ClassSlackBytes -= blockSize - BlockHeader.Size - payloadLength;
        }

        if (region.Allocated > 0)
        {
            // A full region has a free slot again and joins the regions of its class that have one.
            if (wasFull)
            {
                PushFront(ref _regionsWithFreeSlot[sizeClass], regionNumber);
            }

            return;
        }

        // The region lost its last block. It was among the regions of its class with a free slot,
        // unless its only slot was the one just freed.
        if (!wasFull)
        {
            Unlink(ref _regionsWithFreeSlot[sizeClass], regionNumber);
        }

        ReturnRegion((int)address.Segment, segment, regionIndex);
    }

    // A reference to the header of a valid block, in native memory. It refers to the block only
    // until the block is freed or moved or the arena is disposed, and it must not leave the
    // protected scope in which it was obtained (arena protocol, "Lock and lifetime order").
    // ArgumentException when the address is not that of a block of this arena that is allocated and not
    // freed, which is decided without reading through the address; the null address never is one.
    public ref BlockHeader Header(ArenaAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var segment = Locate(address, out _, out _) ?? throw NotABlock(address);

        return ref *(BlockHeader*)(segment.Memory + address.Offset);
    }

    // The payload of a valid block, in native memory: the header's PayloadLength bytes after the
    // header, never beyond the capacity of the slot. The span has the same lifetime as the
    // reference Header returns. ArgumentException when Header refuses the address;
    // InvalidOperationException when the header's length exceeds the capacity of the slot, before
    // any byte of the payload is reached.
    public Span<byte> Payload(ArenaAddress address)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var segment = Locate(address, out var regionIndex, out _) ?? throw NotABlock(address);
        var block = segment.Memory + address.Offset;
        var blockSize = SizeClasses.SizeOf(segment.Regions[regionIndex].SizeClass);
        var payloadLength = ((BlockHeader*)block)->PayloadLength;

        // The length comes from memory a caller may have written; it is checked against the
        // capacity the allocator knows before a span is made over it.
        if (payloadLength > blockSize - BlockHeader.Size)
        {
            throw LengthBeyondSlot(payloadLength, blockSize);
        }

        return new Span<byte>(block + BlockHeader.Size, (int)payloadLength);
    }

    // The counters as they stand at the moment of the call.
    public ArenaCounters Counters
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return new ArenaCounters(
                LiveBytes: _tally.LiveBytes,
                ClassSlackBytes: _tally.ClassSlackBytes,
                PreparedBytes: _tally.PreparedBytes,
                RetiredBytes: _tally.RetiredBytes,
                FreeRegions: _tally.FreeRegionCount,
                ReservedSegments: _reservedSegments.Count,
                Segments: _tally.SegmentCount);
        }
    }

    // Frees every segment, the reserve included. Afterwards every other instance member throws
    // ObjectDisposedException, and a second call does nothing.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var segment in _segments)
        {
            segment?.Release(_allocator);
        }

        Array.Clear(_segments);
    }

    // A region number names a region of any segment: the slot of the segment in the high 32 bits,
    // the index of the region in the low 32 bits. No segment has slot 0, so the number 0 names no
    // region and serves as the end of a list.
    private static long RegionNumber(int segmentSlot, int regionIndex) =>
        ((long)segmentSlot << 32) | (uint)regionIndex;

    private static int SegmentSlotOf(long regionNumber) => (int)(regionNumber >> 32);

    private static int RegionIndexOf(long regionNumber) => (int)regionNumber;

    private static ArgumentException NotABlock(ArenaAddress address) =>
        new($"The address {address} is not that of an allocated block of this arena.", nameof(address));

    private static InvalidOperationException LengthBeyondSlot(uint payloadLength, int blockSize) =>
        new($"The header gives a payload of {payloadLength} bytes, more than the {blockSize - BlockHeader.Size} bytes its slot holds.");

    // Finds the allocated block an address names: its segment, the index of its region and its
    // slot in the region; null when the address names none. It reads only the allocator's
    // metadata, never the memory the address points to, and takes the same few steps for every
    // address: the segment by its slot, the region by the offset, the slot by the class of the
    // region, and the slot's bit.
    private ArenaSegment? Locate(ArenaAddress address, out int regionIndex, out int slot)
    {
        regionIndex = 0;
        slot = 0;

        if (address.Segment >= (uint)_segments.Length || address.Offset >= (uint)_segmentSize)
        {
            return null;
        }

        // Slot 0 of the table never holds a segment, so the null address fails here.
        var segment = _segments[address.Segment];

        if (segment is null)
        {
            return null;
        }

        regionIndex = (int)(address.Offset / ArenaSegment.RegionSize);

        // The class of a region that serves none is the one it served last, or 0 for a region never
        // used; any class gives a slot within the region's bits, and every bit of such a region is
        // clear, so the bit refuses its offsets. The bit of an offset in the tail of a region, too
        // short for a slot, is clear as well.
        var blockSize = SizeClasses.SizeOf(segment.Regions[regionIndex].SizeClass);
        var (slotInRegion, remainder) = Math.DivRem((int)(address.Offset % ArenaSegment.RegionSize), blockSize);

        // An offset inside a slot names no block.
        if (remainder != 0)
        {
            return null;
        }

        slot = slotInRegion;

        return segment.IsAllocated(regionIndex, slot) ? segment : null;
    }

    // Gives the class a region, so that it has one with a free slot: a free region, else the first
    // region of a reserved segment, else the first region of a new segment. False when none of them
    // is there and a new segment would take the arena beyond its memory limit; then nothing has
    // changed.
    private bool TryTakeRegion(int sizeClass)
    {
        long regionNumber;

        if (_tally.FreeRegions != NoRegion)
        {
            regionNumber = _tally.FreeRegions;
            RemoveFreeRegion(regionNumber);
        }
        else if (_reservedSegments.Count > 0)
        {
            regionNumber = PutInUse(_reservedSegments.Pop());
        }
        else if ((_tally.SegmentCount + 1L) * _segmentSize <= _memoryLimit)
        {
            regionNumber = PutInUse(ObtainSegment());
        }
        else
        {
            return false;
        }

        var segment = _segments[SegmentSlotOf(regionNumber)]!;

        segment.Regions[RegionIndexOf(regionNumber)].SizeClass = (byte)sizeClass;
        segment.RegionsInUse++;
        PushFront(ref _regionsWithFreeSlot[sizeClass], regionNumber);

        return true;
    }

    // Obtains a new segment through the allocator, puts it into the table and returns its slot: the
    // slot of a segment freed before when there is one, so that the table does not grow with the
    // number of segments ever obtained. Everything that can fail comes first: growing the table, the
    // segment's metadata and its native memory. Only then does the arena change, so a failure leaves
    // it as it was and holds nothing it obtained.
    private int ObtainSegment()
    {
        var reuseSlot = _freedSegmentSlots.Count > 0;
        var slot = reuseSlot ? _freedSegmentSlots.Peek() : _tally.NextUnusedSegmentSlot;

        if (slot == _segments.Length)
        {
            GrowSegmentTable();
        }

        var segment = new ArenaSegment(_segmentSize, _allocator);

        if (reuseSlot)
        {
            _freedSegmentSlots.Pop();
        }
        else
        {
            _tally.NextUnusedSegmentSlot++;
        }

        _segments[slot] = segment;
        _tally.SegmentCount++;

        return slot;
    }

    // Doubles the segment table, and with it the capacity of the two stacks of slots, which can never
    // hold more slots than the table has.
    private void GrowSegmentTable()
    {
        var grown = new ArenaSegment?[_segments.Length * 2];

        Array.Copy(_segments, grown, _segments.Length);
        _freedSegmentSlots.EnsureCapacity(grown.Length);
        _reservedSegments.EnsureCapacity(grown.Length);

        _segments = grown;
    }

    // An empty segment, new or from the reserve, goes into use: every region but the first becomes a
    // free region, and the first is returned for the class that needs it.
    private long PutInUse(int segmentSlot)
    {
        for (var regionIndex = _regionsPerSegment - 1; regionIndex > 0; regionIndex--)
        {
            AddFreeRegion(RegionNumber(segmentSlot, regionIndex));
        }

        return RegionNumber(segmentSlot, 0);
    }

    // A region that lost its last block returns to its segment and becomes a free region. When it
    // was the segment's last region in use, the segment is empty: its regions leave the free
    // regions, and the segment enters the reserve when the reserve has room and is freed otherwise.
    private void ReturnRegion(int segmentSlot, ArenaSegment segment, int regionIndex)
    {
        segment.RegionsInUse--;

        if (segment.RegionsInUse > 0)
        {
            AddFreeRegion(RegionNumber(segmentSlot, regionIndex));

            return;
        }

        // Every other region of an empty segment is a free region.
        for (var other = 0; other < _regionsPerSegment; other++)
        {
            if (other != regionIndex)
            {
                RemoveFreeRegion(RegionNumber(segmentSlot, other));
            }
        }

        if (_reservedSegments.Count < _reserveLimit)
        {
            _reservedSegments.Push(segmentSlot);

            return;
        }

        // Nothing refers to the segment any more: no block, no list and, after this, no slot of the
        // table. Its native memory goes back to the allocator, and its slot to the next new segment.
        _segments[segmentSlot] = null;
        _freedSegmentSlots.Push(segmentSlot);
        _tally.SegmentCount--;
        segment.Release(_allocator);
    }

    private void AddFreeRegion(long regionNumber)
    {
        PushFront(ref _tally.FreeRegions, regionNumber);
        _tally.FreeRegionCount++;
    }

    private void RemoveFreeRegion(long regionNumber)
    {
        Unlink(ref _tally.FreeRegions, regionNumber);
        _tally.FreeRegionCount--;
    }

    private ref ArenaRegion RegionAt(long regionNumber) =>
        ref _segments[SegmentSlotOf(regionNumber)]!.Regions[RegionIndexOf(regionNumber)];

    // Puts a region that is in no list at the front of the list that starts at first.
    private void PushFront(ref long first, long regionNumber)
    {
        ref var region = ref RegionAt(regionNumber);

        region.Previous = NoRegion;
        region.Next = first;

        if (first != NoRegion)
        {
            RegionAt(first).Previous = regionNumber;
        }

        first = regionNumber;
    }

    // Takes a region out of the list that starts at first, wherever it is in it.
    private void Unlink(ref long first, long regionNumber)
    {
        ref var region = ref RegionAt(regionNumber);

        if (region.Previous == NoRegion)
        {
            first = region.Next;
        }
        else
        {
            RegionAt(region.Previous).Next = region.Next;
        }

        if (region.Next != NoRegion)
        {
            RegionAt(region.Next).Previous = region.Previous;
        }

        region.Previous = NoRegion;
        region.Next = NoRegion;
    }
}
