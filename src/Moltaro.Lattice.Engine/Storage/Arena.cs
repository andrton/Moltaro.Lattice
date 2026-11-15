namespace Moltaro.Lattice.Storage;

// The native memory of one database (milestone contract, "Native layout and allocation"; Q17,
// "Size classes and reusable slots" and "Empty-segment retention and release"). The arena
// obtains segments from the native allocator and divides them into regions. Each region serves
// one size class and tracks its free slots, and the regions of a class that have a free slot are
// kept in a list, so that an allocation finds space without scanning the arena. Every block
// starts with a BlockHeader and is reached by its relative address, which the arena checks
// before it reads anything through it.
//
// In S04 a block is either allocated or free. The arena takes no lock, and one thread at a time
// uses it; the protection of the arena protocol, under which readers and writers share its
// blocks, comes with S06 to S08.
internal sealed class Arena : IDisposable
{
    // Takes the settings; segments are obtained on demand. Refuses null options with
    // ArgumentNullException, and with ArgumentOutOfRangeException a MemoryLimit smaller than one
    // segment, a SegmentSize smaller than MaxBlockSize and a negative ReservedSegments.
    public Arena(ArenaOptions options) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

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
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

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
    // BlockSizeFor refuses is refused in the same way, with ArgumentOutOfRangeException.
    public bool TryAllocate(int payloadLength, BlockKindEnum kind, RootHandle root, out ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Returns the block's slot to its region, where the next allocation of its class can reuse it.
    // A region left without blocks returns to its segment. A segment left empty enters the reserve
    // when the reserve has room, and is otherwise freed at once, inside this call; the background
    // release of Q17 comes with S23. An invalid address, or that of a block already freed, is
    // refused with ArgumentException before anything is read through it.
    public void Free(ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Whether the address is that of a block of this arena that is allocated and not freed. The
    // null address is never valid. Nothing is read through the address to decide this.
    public bool IsValid(ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // A reference to the header of a valid block, in native memory. It refers to the block only
    // until the block is freed or moved or the arena is disposed, and it must not leave the
    // protected scope in which it was obtained (arena protocol, "Lock and lifetime order").
    // ArgumentException when IsValid refuses the address.
    public ref BlockHeader Header(ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The payload of a valid block, in native memory: the header's PayloadLength bytes after the
    // header, never beyond the capacity of the slot. The span has the same lifetime as the
    // reference Header returns. ArgumentException when IsValid refuses the address;
    // InvalidOperationException when the header's length exceeds the capacity of the slot, before
    // any byte of the payload is reached.
    public Span<byte> Payload(ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The counters as they stand at the moment of the call.
    public ArenaCounters Counters =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Frees every segment, the reserve included. Afterwards every other instance member throws
    // ObjectDisposedException, and a second call does nothing.
    public void Dispose() =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");
}
