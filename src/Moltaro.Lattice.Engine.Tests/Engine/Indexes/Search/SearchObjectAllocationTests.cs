using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// One object holds a search, with its key in it once, so that a search that returns one entity allocates
// that object and its result and nothing else (docs/design/ordered-index.md, "The search by a full key").
// Measured as a read by key is, with GC.GetAllocatedBytesForCurrentThread, after a first search of the same
// form, in the test collection of the tests that measure managed allocation: Find of ByNumber of Counted
// (IndexSchema), enumerated to its end, for a key one entity has. Through a class, the search and its
// result take at most 400 bytes. By name the result is a record, larger than an object of the class, so
// what is stated there is the search less a read by key of the same entity on the same path, which
// allocates the same result: the one object of the search, which on both paths is at most 400 bytes less
// the result of the class, measured here as what a read by key through the class allocates. The 400 bytes
// are a ceiling until the search is one object; then the exact figure, and what it is made of, replaces
// it.
[Collection<ManagedAllocationCollection>]
public sealed class SearchObjectAllocationTests
{
    private const long Ceiling = 400;

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task Search_that_returns_one_entity_allocates_one_object_beside_its_result_through_a_class_and_by_name()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();
        var records = database.Set("Counted");

        for (var id = 1L; id <= 100; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
        }

        // The index objects are taken once, as an application keeps them.
        var byClass = counted.Index("ByNumber");
        var byName = records.Index("ByNumber");

        var classResult = Measure(() => counted.Get(50L) is null ? 0 : 1);
        var classSearch = Measure(() => Count(byClass.Find(IndexKey.Equal(50L))));
        var nameResult = Measure(() => records.Get(50L) is null ? 0 : 1);
        var nameSearch = Measure(() => Count(byName.Find(IndexKey.Equal(50L))));

        Assert.True(classResult > 0, "A read by key through the class allocated nothing, not even its result.");
        Assert.True(classSearch <= Ceiling, $"A search through the class allocated {classSearch} bytes, its result {classResult} of them, beyond {Ceiling}.");
        Assert.True(nameSearch - nameResult <= Ceiling - classResult, $"A search by name allocated {nameSearch} bytes, {nameSearch - nameResult} beside its result, beyond the {Ceiling - classResult} the object of a search may take.");
    }

    // What one call allocates on this thread, after a first call; the call returns one entity.
    private static long Measure(Func<int> call)
    {
        Assert.Equal(1, call());

        var start = GC.GetAllocatedBytesForCurrentThread();
        var found = call();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(1, found);

        return allocated;
    }

    // Enumerates the matches as an application does, to the end, without keeping them.
    private static int Count<TEntity>(IEnumerable<TEntity> matches)
    {
        var count = 0;

        foreach (var _ in matches)
        {
            count++;
        }

        return count;
    }
}
