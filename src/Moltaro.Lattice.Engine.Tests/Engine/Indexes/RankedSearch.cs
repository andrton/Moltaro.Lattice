using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// What a search of each index of Ranked finds by the keys of one state of an entity: ByNumber by its
// Number, ByLogin by its Login, ByRegion by its Region in another case, which the index compares without
// case. On a master and on a child alike.
internal static class RankedSearch
{
    // Each of the three searches finds exactly the entities given, by their ids in ascending order.
    public static void ExpectFound(ILatticeDatabase database, IndexSchema.Ranked state, long[] entities)
    {
        var ranked = database.Set<IndexSchema.Ranked>();

        Assert.Equal(entities, ranked.Index("ByNumber").Find(IndexKey.Equal(state.Number)).Select(entity => entity.Id).Order());
        Assert.Equal(entities, ranked.Index("ByLogin").Find(IndexKey.Equal(state.Login)).Select(entity => entity.Id).Order());
        Assert.Equal(entities, ranked.Index("ByRegion").Find(IndexKey.Equal(state.Region.ToUpperInvariant())).Select(entity => entity.Id).Order());
    }
}
