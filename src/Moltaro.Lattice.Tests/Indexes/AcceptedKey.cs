namespace Moltaro.Lattice.Tests.Indexes;

// What a search shows of a key its index accepts, on an entity type the engine does not store yet, such
// as one with owned values. The contract checks a key against the index before anything is searched and
// refuses a key the index cannot serve with ArgumentException; a key it accepts goes on to the search,
// which finds nothing, since no entity of such a type can be written, or is refused with
// NotSupportedException, as a write of such a type is. Either way the key itself was not refused, and
// that is what these tests state.
internal static class AcceptedKey
{
    public static void Expect(Func<IEnumerable<object>> search)
    {
        try
        {
            Assert.Empty(search());
        }
        catch (NotSupportedException)
        {
            // The search of the type is refused after the key was accepted.
        }
    }

    public static async Task ExpectAsync<TEntity>(ILatticeIndex<TEntity> index, IndexKey key)
        where TEntity : class
    {
        try
        {
            Assert.Empty((await index.FindAsync(key, new LatticeFindOptions())).Items);
        }
        catch (NotSupportedException)
        {
            // As in Expect.
        }
    }
}
