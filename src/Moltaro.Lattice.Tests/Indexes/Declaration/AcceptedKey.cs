namespace Moltaro.Lattice.Tests.Indexes.Declaration;

// A key the index accepts reaches the search. These cases use an empty entity set,
// so that search returns no matches (ILatticeIndex.Find, FindAsync).
internal static class AcceptedKey
{
    public static void Expect(Func<IEnumerable<object>> search) => Assert.Empty(search());

    public static async Task ExpectAsync<TEntity>(ILatticeIndex<TEntity> index, IndexKey key)
        where TEntity : class
    {
        Assert.Empty((await index.FindAsync(key, new LatticeFindOptions())).Items);
    }
}
