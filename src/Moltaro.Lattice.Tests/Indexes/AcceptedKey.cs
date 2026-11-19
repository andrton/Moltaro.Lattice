namespace Moltaro.Lattice.Tests.Indexes;

// What a search shows of a key its index accepts, on an entity type the engine cannot store yet, such as
// one with owned values. The contract checks a key against the index before anything is searched and
// refuses a key the index cannot serve with ArgumentException; a key it accepts goes on to the search,
// which answers as a read by key does on such a type: it finds nothing (ILatticeIndex.Find, FindAsync).
// Only a write of the type is refused.
internal static class AcceptedKey
{
    public static void Expect(Func<IEnumerable<object>> search) => Assert.Empty(search());

    public static async Task ExpectAsync<TEntity>(ILatticeIndex<TEntity> index, IndexKey key)
        where TEntity : class
    {
        Assert.Empty((await index.FindAsync(key, new LatticeFindOptions())).Items);
    }
}
