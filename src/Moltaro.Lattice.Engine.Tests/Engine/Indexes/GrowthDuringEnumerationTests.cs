using Moltaro.Lattice.TestSupport.Findings;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// An enumeration that a growth of the primary index meets halfway never returns an entity twice and
// never ends as complete without one (review round 1 of Epic 1, sprint S08, part 1: a step read the
// version of the index and then its table, and the growth published the new table before it changed
// the version, so a step could apply a position of the old table to the new one). The contract of S06
// stands in part 1: an enumeration during which the index changed fails, from the step that sees the
// change, with NotSupportedException, and what it returned before stays valid; it never returns a set
// it cannot vouch for.
//
// The test fills the index until the next insert grows it, which a database of its own with the same
// keys shows, since the index of the same keys grows at the same insert. It enumerates half of the
// entities, then lets that insert grow the index and holds the growth between the store that
// publishes the grown table and the change of the version, through the switch of the engine
// (EngineDatabase.HoldNextGrowth). While the growth is held there it takes the rest of the steps: each
// returns an entity or fails. Then it releases the growth. What the enumeration returned holds no key
// twice; if it ended, rather than failed, it returned every entity there was, each once. Every wait is
// bounded.
public sealed class GrowthDuringEnumerationTests
{
    // The growth the test meets: the third, so that the half of the entities it enumerates first lies
    // in several buckets of the table.
    private const int Growth = 3;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact(Skip = OpenFindings.ReviewRound1)]
    public async Task Enumeration_that_a_growth_of_the_index_meets_halfway_returns_no_entity_twice_and_never_ends_without_one()
    {
        var full = await KeysBeforeGrowthAsync(Growth);
        var database = await ClashSchema.OpenAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var clashes = database.Set<ClashSchema.Clash>();

            for (var key = 0L; key < full; key++)
            {
                await clashes.InsertAsync(ClashSchema.Of(key));
            }

            using var enumerator = clashes.Enumerate().GetEnumerator();
            var returned = new List<long>();

            while (returned.Count < full / 2 && enumerator.MoveNext())
            {
                returned.Add(enumerator.Current.Id);
            }

            // The next insert grows the index, and the switch holds the growth halfway.
            var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            database.HoldNextGrowth(clashes.EntityType, () =>
            {
                inside.TrySetResult();

                // On the thread of the write, until the test releases it.
                release.Task.Wait(Bound);
            });

            var growing = Task.Run(() => clashes.InsertAsync(ClashSchema.Of(full)).AsTask());

            await Task.WhenAny(inside.Task, growing).WaitAsync(Bound);
            Assert.True(inside.Task.IsCompleted, "The insert did not grow the index.");

            var ended = false;

            try
            {
                while (enumerator.MoveNext())
                {
                    returned.Add(enumerator.Current.Id);
                }

                ended = true;
            }
            catch (NotSupportedException)
            {
                // The enumeration saw the change and failed, as an enumeration during which the index
                // changed does.
            }
            finally
            {
                release.TrySetResult();
            }

            await growing.WaitAsync(Bound);

            Assert.Equal(returned.Count, returned.Distinct().Count());

            if (ended)
            {
                Assert.Equal(Enumerable.Range(0, full).Select(key => (long)key), returned.Order());
            }
        }
        finally
        {
            release.TrySetResult();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // How many of the keys 0, 1, 2, ... a database holds when the insert of the next one makes its
    // index grow for the given time.
    private static async Task<int> KeysBeforeGrowthAsync(int growth)
    {
        await using var database = await ClashSchema.OpenAsync();
        var clashes = database.Set<ClashSchema.Clash>();
        var bytes = database.Counters.PrimaryIndexBytes;
        var growths = 0;

        for (var key = 0; ; key++)
        {
            await clashes.InsertAsync(ClashSchema.Of(key));

            var grown = database.Counters.PrimaryIndexBytes;

            if (grown != bytes && ++growths == growth)
            {
                return key;
            }

            bytes = grown;
        }
    }
}
