using System.Numerics;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

// Native memory for the nodes of the candidates: a model of the engine's arena
// (src/Moltaro.Lattice.Engine/Storage/Arena.cs) reduced to what a comparison of index structures
// needs. Segments of native memory; a relative address of eight bytes, the segment in the high 32
// bits and the byte offset of the block in the low 32, 0 being null; a header of 32 bytes in front
// of every block, as the engine's BlockHeader; the size classes of the engine, 16-byte steps up to
// 1 KiB, 256-byte steps up to 4 KiB, then powers of two up to 64 KiB, the header included; a free
// list for each class. The counts are of whole slots, as the engine counts them: live, prepared
// (taken for a write not published yet), retired (unlinked, waiting for the readers).
//
// One thread allocates, retires and frees: the writer. Readers only turn an address into memory
// (Payload), through the table of segments, whose element for a segment is written before any
// address in that segment is published by a store with release semantics.
//
// What it leaves out of the engine's arena: regions per size class, the validation of an address
// against the allocator's metadata before a read (the engine's Arena.Payload checks the segment,
// the region and the length; an index walk would take a path without those checks), the memory
// limit and the return of empty segments.
internal sealed unsafe class NodeArena : IDisposable
{
    public const int HeaderBytes = 32;

    public const int MaxBlockBytes = 64 * 1024;

    public const int SegmentBytes = 4 * 1024 * 1024;

    // Enough for the measurements of this prototype: 32 GiB of segments.
    private const int MostSegments = 8192;

    // 64 classes of 16-byte steps, 12 of 256-byte steps, 4 powers of two.
    private const int ClassCount = 80;

    // Where the payload length and the size class stand in the header.
    private const int PayloadLengthOffset = 20;

    private const int ClassOffset = 24;

    private readonly nint[] _segments = new nint[MostSegments];

    private readonly ulong[] _free = new ulong[ClassCount];

    private int _segmentCount;

    private int _usedInLast = SegmentBytes;

    private bool _disposed;

    // Slot bytes of blocks that are live, prepared and retired.
    public long LiveBytes { get; private set; }

    public long PreparedBytes { get; private set; }

    public long RetiredBytes { get; private set; }

    // How many blocks were taken from the free lists or the segments: a publication that takes
    // none allocated nothing.
    public long Allocations { get; private set; }

    public long SegmentBytesHeld => (long)_segmentCount * SegmentBytes;

    // The slot of a block with this payload, header included.
    public static int BlockBytesFor(int payloadLength) => SizeOf(ClassOf(payloadLength + HeaderBytes));

    // A block for payloadLength bytes, live at once (prepared: false) or prepared until Publish.
    public ulong Allocate(int payloadLength, bool prepared)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadLength + HeaderBytes, MaxBlockBytes);

        var sizeClass = ClassOf(payloadLength + HeaderBytes);
        var slot = SizeOf(sizeClass);
        var address = _free[sizeClass];

        if (address != 0)
        {
            _free[sizeClass] = *(ulong*)Block(address);
        }
        else
        {
            if (_usedInLast + slot > SegmentBytes)
            {
                AddSegment();
            }

            address = ((ulong)_segmentCount << 32) | (uint)_usedInLast;
            _usedInLast += slot;
        }

        var block = Block(address);

        new Span<byte>(block, HeaderBytes).Clear();
        *(int*)(block + PayloadLengthOffset) = payloadLength;
        *(int*)(block + ClassOffset) = sizeClass;

        if (prepared)
        {
            PreparedBytes += slot;
        }
        else
        {
            LiveBytes += slot;
        }

        Allocations++;

        return address;
    }

    // A prepared block becomes live: what a publication does, without allocating.
    public void Publish(ulong address)
    {
        var slot = SlotOf(address);

        PreparedBytes -= slot;
        LiveBytes += slot;
    }

    // A live block is unlinked and waits for the readers that could still reach it.
    public void Retire(ulong address)
    {
        var slot = SlotOf(address);

        LiveBytes -= slot;
        RetiredBytes += slot;
    }

    // Frees a block in the state given: never reached by a reader (prepared, or live in a structure
    // that no reader sees), or retired and drained.
    public void Free(ulong address, BlockStateEnum state)
    {
        var slot = SlotOf(address);

        switch (state)
        {
            case BlockStateEnum.Prepared:
                PreparedBytes -= slot;
                break;

            case BlockStateEnum.Retired:
                RetiredBytes -= slot;
                break;

            default:
                LiveBytes -= slot;
                break;
        }

        var sizeClass = *(int*)(Block(address) + ClassOffset);

        // The link of the free list lies in the header, which every block has.
        *(ulong*)Block(address) = _free[sizeClass];
        _free[sizeClass] = address;
    }

    // The payload of a block: for readers and the writer alike.
    public byte* Payload(ulong address) => (byte*)_segments[(int)(address >> 32)] + (uint)address + HeaderBytes;

    // The payload length the block was allocated with.
    public int PayloadLength(ulong address) => *(int*)(Block(address) + PayloadLengthOffset);

    public int SlotOf(ulong address) => SizeOf(*(int*)(Block(address) + ClassOffset));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        for (var segment = 1; segment <= _segmentCount; segment++)
        {
            NativeMemory.AlignedFree((void*)_segments[segment]);
            _segments[segment] = 0;
        }
    }

    private byte* Block(ulong address) => (byte*)_segments[(int)(address >> 32)] + (uint)address;

    private void AddSegment()
    {
        if (_segmentCount + 1 >= MostSegments)
        {
            throw new OutOfMemoryException("The prototype's arena holds no more segments.");
        }

        var memory = NativeMemory.AlignedAlloc(SegmentBytes, 64);

        _segmentCount++;
        Volatile.Write(ref _segments[_segmentCount], (nint)memory);
        _usedInLast = 0;
    }

    private static int ClassOf(int blockBytes)
    {
        if (blockBytes <= 1024)
        {
            return ((blockBytes + 15) / 16) - 1;
        }

        if (blockBytes <= 4096)
        {
            return 64 + ((blockBytes - 1024 + 255) / 256) - 1;
        }

        return 76 + BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)blockBytes)) - 13;
    }

    private static int SizeOf(int sizeClass)
    {
        if (sizeClass < 64)
        {
            return (sizeClass + 1) * 16;
        }

        if (sizeClass < 76)
        {
            return 1024 + ((sizeClass - 63) * 256);
        }

        return 1 << (sizeClass - 76 + 13);
    }
}
