using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// The standing reserve of the indexes of a database as the tests state it: for each index, the need of
// one change of key at its tree as it is now (ReservationNeed), in whole nodes of the index. While no write
// of an index waits, its pool holds at most that, and it is not given back before the database closes
// (docs/design/ordered-index.md, "The reservation"). The tree is read through the test member of the engine
// (IndexContents): its height, and the room of its root, the inserts the root takes before it can split —
// none for an empty tree, otherwise the separators an inner node holds before it is full less the items of
// the root, at least none. While writes wait, a pool keeps the lesser bound of what they can take, the
// lesser of their need at the tree and their linear bound (ReservationNeed.Of, ReservationNeed.NodesBound),
// and gives back what is above it when another write is withdrawn.
internal static class IndexReserve
{
    // The bytes of the reserves of the indexes of the entity type.
    public static long BytesOf(EngineDatabase database, string entityType)
    {
        Assert.True(database.Schema.TryGetEntityType(entityType, out var type));

        var roots = RootLayout.Compile(type, database.Schema);
        var bytes = 0L;

        foreach (var index in type.Indexes)
        {
            var layout = IndexLayout.Compile(index, roots);
            var tree = IndexContents.Of(database, entityType, index.Name);
            var nodes = ReservationNeed.OfOneChangeOfKey(tree.Height, RoomOf(tree, layout.LeastSeparators), layout.LeastSeparators);

            bytes += nodes * layout.NodeBytes;
        }

        return bytes;
    }

    // The bytes of the lesser bound of the inserts and removes given at the tree of each index of the entity
    // type as it is now, in whole nodes of the index: what its pool keeps for writes that wait with those
    // changes, the tree as it was when they were reserved.
    public static long LesserBoundBytesOf(EngineDatabase database, string entityType, int inserts, int removes)
    {
        Assert.True(database.Schema.TryGetEntityType(entityType, out var type));

        var roots = RootLayout.Compile(type, database.Schema);
        var bytes = 0L;

        foreach (var index in type.Indexes)
        {
            var layout = IndexLayout.Compile(index, roots);
            var tree = IndexContents.Of(database, entityType, index.Name);
            var need = ReservationNeed.Of(tree.Height, RoomOf(tree, layout.LeastSeparators), layout.LeastSeparators, inserts, removes);

            bytes += Math.Min(need, ReservationNeed.NodesBound(tree.Height, inserts, removes)) * layout.NodeBytes;
        }

        return bytes;
    }

    // The bytes of the reserves of every index of the schema.
    public static long BytesOfAll(EngineDatabase database) =>
        database.Schema.EntityTypes.Sum(type => BytesOf(database, type.Name));

    private static int RoomOf(IndexContents tree, int leastSeparators)
    {
        if (tree.Height == 0)
        {
            return 0;
        }

        var items = tree.Height == 1 ? tree.LeafCounts[0] : tree.RootSeparators.Count;

        return Math.Max(0, leastSeparators - items);
    }
}
