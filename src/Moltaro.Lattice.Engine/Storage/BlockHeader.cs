using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage;

// The first 32 bytes of every block of the arena, read and written in place in native memory.
// The fields, their widths and their order are those of the milestone contract ("Native layout
// and allocation"), and a test asserts the size. Arena.TryAllocatePrepared writes the whole header. What
// the header does not hold, the allocator keeps in its own metadata: the size class of the slot,
// its capacity and its state.
//
// Sequential layout, because the declared order is the order of the bytes in native memory. No
// Pack: in this order every field already sits on its natural boundary, so natural alignment adds
// no padding, and an 8-byte field is aligned whenever its block is.
[StructLayout(LayoutKind.Sequential)]
internal struct BlockHeader
{
    // The bytes of a header, which come before the payload in every block: the size of this struct,
    // which a test asserts, as a constant for the arithmetic of slots.
    public const int Size = 32;

    // The root that owns the block. A block of kind SharedText names the text entry that owns it
    // in this place instead (Values.ValueBlocks.TryStoreText, TextSlotOf).
    public RootHandle Root;

    // The block that holds the descriptor referring to this one. Null for a root.
    public ArenaAddress Parent;

    // The byte offset of that descriptor from the start of the parent block, its header included.
    // With Parent it lets a move find and repair the one descriptor that refers to this block. A
    // root has no parent, and its offset is 0.
    public uint DescriptorOffset;

    // The bytes of payload in use after the header. The slot may be larger; what the header and
    // the payload leave of it is class slack.
    public uint PayloadLength;

    // What the block holds.
    public BlockKindEnum Kind;

    // Flags of the block. The milestone contract gives them two bytes and defines none yet.
    public ushort Flags;

    // The version of the layout the payload was written in.
    public uint LayoutVersion;
}
