using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// A search by the full key is a step at a time and holds between two steps only the key and the identity
// of the last entity it returned: no node, no address and no slot of the protection of reads
// (Entities.IndexSearch; docs/design/ordered-index.md, "The search by a full key").
// - A search paused after its first entity, its enumerator kept, holds back nothing that writes retire:
//   while it is paused, writes replace nodes of its index and roots of its key, and a drain frees all of
//   them. Resumed, it returns every entity that had the key throughout, no entity twice, and only entities
//   that have the key.
// - A search abandoned between two steps is held by nothing of the database: once the test lets go of its
//   enumerator, the enumerator is collected, while the database stays open.
public sealed class PausedSearchTests
{
    private const int Entities = 50;

    [Fact(Skip = SkipReason.BeforeSprint14)]
    public async Task Search_paused_between_two_steps_holds_back_nothing_that_writes_retire_and_resumes_with_the_entities_that_kept_the_key()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        // Entities 1 to 50 have the key and will leave it, 51 to 100 have other keys, and 101 to 110 have
        // the key throughout.
        for (var id = 1L; id <= (2 * Entities) + 10; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id <= Entities || id > 2 * Entities ? 7 : id });
        }

        using var search = counted.Index("ByNumber").Find(IndexKey.Equal(7L)).GetEnumerator();

        Assert.True(search.MoveNext());
        var first = search.Current.Id;

        // Entities of the key leave it and come back, others move: nodes and roots are retired.
        for (var id = 1L; id <= 2 * Entities; id++)
        {
            await counted.ReplaceAsync(new IndexSchema.Counted { Id = id, Number = id % 2 == 0 && id <= Entities ? 8 : 9_000 + id });
        }

        for (var id = 2L; id <= Entities; id += 2)
        {
            await counted.ReplaceAsync(new IndexSchema.Counted { Id = id, Number = 7 });
        }

        database.DrainRetired();

        Assert.Equal(0, database.Counters.Arena.RetiredBytes);

        var rest = new List<long>();

        while (search.MoveNext())
        {
            Assert.Equal(7, search.Current.Number);
            rest.Add(search.Current.Id);
        }

        Assert.Equal(rest.Count, rest.Distinct().Count());
        Assert.DoesNotContain(first, rest);
        Assert.All(rest, id => Assert.True((id % 2 == 0 && id <= Entities) || id > 2 * Entities, $"The search returned {id}, which does not have the key."));
        Assert.All(Enumerable.Range((2 * Entities) + 1, 10).Select(id => (long)id).Where(id => id != first), id => Assert.Contains(id, rest));
    }

    [Fact(Skip = SkipReason.BeforeSprint14)]
    public async Task Search_abandoned_between_two_steps_is_held_by_nothing_of_the_database()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= Entities; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 7 });
        }

        var abandoned = Abandon(counted);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(abandoned.IsAlive, "The database holds a search that was abandoned between two steps.");
        Assert.Equal(Entities, counted.Index("ByNumber").Find(IndexKey.Equal(7L)).Count());
        GC.KeepAlive(database);
    }

    // In a frame of its own, so that nothing of the test's frame references it: a search that took one step
    // and was let go of, without its enumerator being disposed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(ILatticeSet<IndexSchema.Counted> counted)
    {
        var search = counted.Index("ByNumber").Find(IndexKey.Equal(7L)).GetEnumerator();

        Assert.True(search.MoveNext());

        return new WeakReference(search);
    }
}
