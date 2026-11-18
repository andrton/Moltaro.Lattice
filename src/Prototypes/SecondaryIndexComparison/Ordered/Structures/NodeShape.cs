using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

// The layout of a node of the two B+trees in one block of the arena. The payload begins with a header
// of 16 bytes: the count (4), whether it is a leaf (4), a version (8, used by the optimistic tree
// only). A leaf then holds Count entries. An inner node holds Count separators, then, aligned to eight
// bytes, Count + 1 children: relative addresses of eight bytes, each read and written whole. Separator
// i is a lower bound of child i + 1 and above every entry of child i.
internal readonly struct NodeShape
{
    public const int HeaderBytes = 16;

    public NodeShape(int blockBytes, int entryBytes)
    {
        if (NodeArena.BlockBytesFor(blockBytes - NodeArena.HeaderBytes) != blockBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(blockBytes), blockBytes, "A node is one size class of the arena.");
        }

        BlockBytes = blockBytes;
        PayloadBytes = blockBytes - NodeArena.HeaderBytes;
        LeafCapacity = (PayloadBytes - HeaderBytes) / entryBytes;

        var inner = (PayloadBytes - HeaderBytes - 8) / (entryBytes + 8);

        while (AlignUp(HeaderBytes + (inner * entryBytes)) + ((inner + 1) * 8) > PayloadBytes)
        {
            inner--;
        }

        InnerCapacity = inner;
        ChildrenOffset = AlignUp(HeaderBytes + (inner * entryBytes));

        if (LeafCapacity < 4 || InnerCapacity < 4)
        {
            throw new ArgumentOutOfRangeException(nameof(blockBytes), blockBytes, "A node holds at least four entries.");
        }
    }

    public int BlockBytes { get; }

    public int PayloadBytes { get; }

    public int LeafCapacity { get; }

    // The most separators of an inner node; it has one more child.
    public int InnerCapacity { get; }

    public int ChildrenOffset { get; }

    private static int AlignUp(int bytes) => (bytes + 7) & ~7;
}
