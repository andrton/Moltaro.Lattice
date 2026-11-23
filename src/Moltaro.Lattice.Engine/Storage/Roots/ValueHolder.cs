using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage.Roots;

// The block that a writer fills with structures and with the values their descriptors name inline, and where
// the other values go (NestedValues): the image of a root that its writer collects before the root block
// exists, or a block of the records of a List. It holds the bytes of the block, the room left in it for
// values inline, the arena and the owner of the blocks that the values out of line take, and what the
// writing has taken so far. A ref struct on the stack of one write, passed by reference: the writing of one
// block changes it, and the caller takes back what it counted.
internal ref struct ValueHolder
{
    // A holder of the bytes given, which lie at the offset origin of the block, its header counted, so that
    // the byte at the offset o of the block is bytes[o - origin]; the first value inline goes at the offset
    // inlineEnd. The address is that of the block, which every block of a value out of line names as its
    // parent; the null address for the image of a root, whose writer names the root block as that parent
    // when it completes the root (RootWriter.Complete).
    public ValueHolder(Arena arena, RootHandle root, uint layoutVersion, LatticeStringEncodingEnum encoding, Span<byte> bytes, int origin, ArenaAddress address, int inlineEnd)
    {
        Arena = arena;
        Root = root;
        LayoutVersion = layoutVersion;
        Encoding = encoding;
        Bytes = bytes;
        Origin = origin;
        Address = address;
        InlineEnd = inlineEnd;
    }

    // The arena of the blocks; null for a holder of one packed record, which holds no descriptor and so
    // takes nothing of an arena (PackedOwnedValues).
    public Arena Arena { get; }

    // The root that every block the writing takes belongs to, and the layout version their headers carry.
    public RootHandle Root { get; }

    public uint LayoutVersion { get; }

    // The encoding of the database, in which text is stored.
    public LatticeStringEncodingEnum Encoding { get; }

    // The bytes of the block from the offset Origin on.
    public Span<byte> Bytes { get; }

    public int Origin { get; }

    // The block, or the null address for the image of a root.
    public ArenaAddress Address { get; }

    // The offset of the block, header counted, where the next value inline goes; the room for values inline
    // ends with Bytes.
    public int InlineEnd { get; set; }

    // The end of the room, as InlineEnd counts.
    public readonly int End => Origin + Bytes.Length;

    // The bytes of the slots of the blocks the writing took, and of the entries of the text table it added.
    public long SlotBytes { get; set; }

    // The arena refused a block: nothing more is allocated, and the write is refused for its memory.
    public bool Refused { get; set; }

    // The bytes of the block at the offset given, header counted, for the length given.
    public readonly Span<byte> At(int offset, int length) => Bytes.Slice(offset - Origin, length);
}
