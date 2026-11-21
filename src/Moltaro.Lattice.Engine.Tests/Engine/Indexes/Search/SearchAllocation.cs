namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// How the tests of what a search allocates measure it: what one call allocates on the calling thread,
// with GC.GetAllocatedBytesForCurrentThread, after a first call of the same form, and a search enumerated
// as an application does, to its end, without keeping what it returns.
internal static class SearchAllocation
{
    // What one call allocates on this thread, after a first call; the call returns the number of entities
    // found, which both calls check.
    public static long Measure(Func<int> call, int found)
    {
        Assert.Equal(found, call());

        var start = GC.GetAllocatedBytesForCurrentThread();
        var count = call();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(found, count);

        return allocated;
    }

    // Enumerates the matches as an application does, to the end, without keeping them.
    public static int Count<TEntity>(IEnumerable<TEntity> matches)
    {
        var count = 0;

        foreach (var _ in matches)
        {
            count++;
        }

        return count;
    }
}
