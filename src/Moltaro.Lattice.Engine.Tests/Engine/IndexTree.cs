namespace Moltaro.Lattice.Engine.Tests.Engine;

// The tree of an index of one part as the engine holds it, read through the test member of the engine
// (EngineDatabase.IndexNodes): the value of each entry of each leaf, the leaves in the order of the index;
// the values of the separators of every inner node; and the height of the tree. For the tests of unique
// indexes in the engine, of the master and of a child, that choose values at the boundaries of the leaves.
internal sealed record IndexTree<TValue>(List<List<TValue>> Leaves, List<TValue> Separators, int Height)
{
    public static IndexTree<TValue> Of(EngineDatabase database, string entityType, string index)
    {
        Assert.True(database.Schema.TryGetEntityType(entityType, out var type));

        var nodes = database.IndexNodes(type, index);
        var leaves = nodes.Where(node => node.Level == 0).Select(node => node.Items.Select(item => (TValue)item.Key[0]!).ToList()).ToList();
        var separators = nodes.Where(node => node.Level > 0).SelectMany(node => node.Items.Select(item => (TValue)item.Key[0]!)).ToList();

        return new IndexTree<TValue>(leaves, separators, nodes.Max(node => node.Level) + 1);
    }
}
