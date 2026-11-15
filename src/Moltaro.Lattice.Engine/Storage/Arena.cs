namespace Moltaro.Lattice.Storage;

// The native memory of one database (milestone contract, "Native layout and allocation"; Q17,
// "Size classes and reusable slots" and "Empty-segment retention and release"). The arena
// obtains segments from the native allocator and divides them into regions. Each region serves
// one size class and tracks its free slots, and the regions of a class that have a free slot are
// kept in a list, so that an allocation finds space without scanning the arena. Every block
// starts with a BlockHeader and is reached by its relative address, which the arena checks
// before it reads anything through it.
//
// In S04 the arena takes no lock, and one thread at a time uses it. The protection of the arena
// protocol, under which readers and writers share its blocks, comes with later sprints.
internal sealed class Arena : IDisposable
{
    // Takes the settings. The arena holds no segment before its first allocation needs one.
    public Arena(ArenaOptions options) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The largest size class, header included. A value too large for one block is stored in
    // chunks, each of which fits one block.
    public const int MaxBlockSize = 64 * 1024;

    // The size class of a block with this payload: the smallest class that holds the 32-byte
    // header and payloadLength bytes. The classes are 16-byte steps up to 1 KiB, 256-byte steps up
    // to 4 KiB, then powers of two up to MaxBlockSize. A negative length, or one that does not fit
    // MaxBlockSize with the header, is refused with ArgumentOutOfRangeException.
    public static int BlockSizeFor(int payloadLength) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Allocates a block for payloadLength bytes of payload, in the size class BlockSizeFor gives,
    // and writes the root, the kind and the payload length into its header. Space the arena
    // already holds comes first: a free slot of that class, then a region that serves no class,
    // then a reserved segment. A new segment is obtained only when none of them can serve.
    //
    // Returns false, with the null address and the counters unchanged, when the allocation would
    // take the segment capacity of the arena beyond ArenaOptions.MemoryLimit. A length that
    // BlockSizeFor refuses is refused in the same way, before anything is allocated.
    public bool TryAllocate(int payloadLength, BlockKindEnum kind, RootHandle root, out ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Returns the block's slot to its region, where the next allocation of its class can reuse it.
    // A region left without blocks returns to its segment; a segment left empty enters the reserve
    // when the reserve has room, and is freed otherwise. An address that IsValid refuses, among
    // them that of a block already freed, is refused with ArgumentException before anything is
    // read through it.
    public void Free(ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Whether the address is that of a live block of this arena: its segment belongs to the arena,
    // its offset is the start of a slot, and the slot holds a block that has not been freed. The
    // null address is never valid. Nothing is read through the address to decide this.
    public bool IsValid(ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // A reference to the header of a valid block, in native memory. It refers to the block only
    // until the block is freed or moved or the arena is disposed, and it must not leave the
    // protected scope in which it was obtained (arena protocol, "Lock and lifetime order").
    // ArgumentException when IsValid refuses the address.
    public ref BlockHeader Header(ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The payload of a valid block, in native memory: the PayloadLength bytes after its header.
    // The span has the same lifetime as the reference Header returns. ArgumentException when
    // IsValid refuses the address.
    public Span<byte> Payload(ArenaAddress address) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The counters as they stand at the moment of the call.
    public ArenaCounters Counters =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Frees every segment, the reserve included. Afterwards every other instance member throws
    // ObjectDisposedException, and a second call does nothing. Whoever disposes the arena makes
    // sure that no reference to a header or a payload is still in use.
    public void Dispose() =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");
}
