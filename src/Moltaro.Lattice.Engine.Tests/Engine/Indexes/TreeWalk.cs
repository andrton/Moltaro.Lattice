using Moltaro.Lattice.Engine.Indexes;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// What the tree of an index holds, read by its walk (OrderedTree.Nodes), for the tests that work the parts
// of an index themselves: its levels, its entries and the bytes of its live nodes. The walk reaches every
// live node from the root, and each node is one block of the arena of the size of a node of its index.
// IndexContents reads the same through a database.
internal static class TreeWalk
{
    // The levels of the tree: 0 when it holds no node, 1 when its root is a leaf.
    public static int HeightOf(SecondaryIndex index)
    {
        var nodes = index.Tree.Nodes();

        return nodes.Count == 0 ? 0 : nodes[0].Level + 1;
    }

    // The entries of its leaves.
    public static long CountOf(SecondaryIndex index) =>
        index.Tree.Nodes().Where(node => node.Level == 0).Sum(node => (long)node.Items.Count);

    // The bytes of its live nodes.
    public static long BytesOf(SecondaryIndex index) =>
        (long)index.Tree.Nodes().Count * index.Layout.NodeBytes;
}
