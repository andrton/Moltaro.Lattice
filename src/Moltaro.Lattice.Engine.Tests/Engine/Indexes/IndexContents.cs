namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// What the tree of one secondary index of a database holds, as the engine shows it to its tests
// (EngineDatabase.IndexNodes, exception X3): every live node, in the order of a walk from the root, a node
// before its children and the children of an inner node in their order, with its level, 0 for a leaf, and
// its items — the entries of a leaf, each with the primary key of the entity its identity names, and the
// separators of an inner node. A key is the values of the parts, as a record of the entity type holds
// them. Taken under the lock of the writes between two changes of the engine; it does not drain, so a
// test drains first when it states what a drain leaves.
internal sealed class IndexContents
{
    private readonly List<(int Level, List<(object?[] Key, object? Entity)> Items)> _nodes;

    private IndexContents(List<(int Level, List<(object?[] Key, object? Entity)> Items)> nodes)
    {
        _nodes = nodes;
    }

    // The live nodes of the tree; each is one block of the arena of the size of a node of its index.
    public int Nodes => _nodes.Count;

    // The levels of the tree: 0 when it has no node, 1 when its root is a leaf.
    public int Height => _nodes.Count == 0 ? 0 : _nodes[0].Level + 1;

    // The entries of the leaves, in the order of the tree.
    public List<(object?[] Key, long Entity)> Entries =>
        [.. _nodes.Where(node => node.Level == 0).SelectMany(node => node.Items).Select(item => (item.Key, Convert.ToInt64(item.Entity!, System.Globalization.CultureInfo.InvariantCulture)))];

    // How many entries each leaf holds, in the order of the tree.
    public List<int> LeafCounts => [.. _nodes.Where(node => node.Level == 0).Select(node => node.Items.Count)];

    // The keys of every item of every node, the separators of inner nodes with the entries of the leaves:
    // what a node holds a reference to the text of when a part is shared.
    public IEnumerable<object?[]> KeysInNodes => _nodes.SelectMany(node => node.Items).Select(item => item.Key);

    // The separators of the root when it is an inner node, empty otherwise.
    public List<object?[]> RootSeparators => _nodes.Count == 0 || _nodes[0].Level == 0 ? [] : [.. _nodes[0].Items.Select(item => item.Key)];

    // Every node in the order of the walk, as one line of its level and its items, each key with the entity
    // of an entry: the whole tree, for a test that states that it did not change.
    public List<string> Walk =>
        [.. _nodes.Select(node => $"{node.Level}: {string.Join("; ", node.Items.Select(item => $"({string.Join(", ", item.Key)}) {item.Entity}"))}")];

    public static IndexContents Of(EngineDatabase database, string entityType, string index)
    {
        Assert.True(database.Schema.TryGetEntityType(entityType, out var type));

        return new IndexContents(database.IndexNodes(type, index));
    }
}
