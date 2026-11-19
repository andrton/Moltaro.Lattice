using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Ordered;

// The first 8 bytes of the payload of a node of an ordered index, after the 32-byte BlockHeader of its
// block (docs/design/ordered-index.md, "Nodes in the arena"). The writer writes it once, when it writes
// the node, before the store that publishes the node; it is never written again, so a reader reads it
// with plain loads. A test asserts the size.
//
// Sequential layout, because the declared order is the order of the bytes in native memory. In this
// order every field sits on its natural boundary, and the 8 bytes keep the children of an inner node,
// which follow the header, on 8-byte boundaries: the payload of a block starts on a 16-byte boundary.
[StructLayout(LayoutKind.Sequential)]
internal struct NodeHeader
{
    // The bytes of the header, which come first in the payload of every node.
    public const int Size = 8;

    // The entries of a leaf, or the separators of an inner node, which has one child more.
    public ushort Count;

    // 0 for a leaf; an inner node is one level above its children, so the root has the height of the
    // tree less one.
    public byte Level;

    // Flags of the node; none is defined.
    public byte Flags;

    // The bytes the entries or the separators take, after the header, the children and the offsets:
    // what the writer needs to know the room a node has left without walking its entries.
    public ushort Bytes;

    // Zero; the header keeps the alignment of the children.
    public ushort Reserved;
}
