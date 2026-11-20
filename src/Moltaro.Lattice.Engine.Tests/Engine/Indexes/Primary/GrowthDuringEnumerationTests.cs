namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Primary;

// An enumeration that a growth of the primary index meets halfway returns every entity once and ends
// (review round 1 of Epic 1, sprint S08, part 1: a step read the version of the index and then its
// table, and the growth published the new table before it changed the version, so a step could apply
// a position of the old table to the new one). In part 1 the contract of S06 stood, and the test
// stated that such an enumeration returned no entity twice and failed rather than end without one;
// from part 2 a growth or a change of the index during the pass no longer fails it (brief of S08,
// Acceptance, and the notes of the coordinator for part 2), so the test states what the pass returns.
//
// The test fills the index until the next insert grows it, which a database of its own with the same
// keys shows, since the index grows by the count of its entries, at the same insert whatever the seed.
// It enumerates half of the entities, then lets that insert grow the index and holds the growth after
// it has published the grown table and before it retires the old one, through the switch of the engine
// (EngineDatabase.HoldNextGrowth). While the growth is held there it takes the rest of the steps, on a
// thread of its own: they find the grown table, of another version than their position, and walk it
// from its start, skipping the keys the pass has returned (PassAcrossGrowthTests states the same for
// larger tables and every key type). The test lets the growth go once the steps have ended, or after a
// short while if they have not, so that the pass ends either way; what the test asserts does not
// depend on which. Every entity that was there before the growth is returned once; the one whose
// insert grew the index, during the pass, at most once. Every wait is bounded.
public sealed class GrowthDuringEnumerationTests
{
    // The growth the test meets: the third, so that the half of the entities it enumerates first lies
    // in several buckets of the table.
    private const int Growth = 3;

    // The most keys the probe inserts before it gives up: the main table grows by the count, so the
    // third growth comes after a few dozen.
    private const int MostKeys = 10_000;

    // How long the steps may take while the growth is held before the test lets it go.
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Enumeration_that_a_growth_of_the_index_meets_halfway_returns_every_entity_once_and_ends()
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

            var rest = OwnThread.Run(
                () =>
                {
                    var taken = new List<long>();

                    while (enumerator.MoveNext())
                    {
                        taken.Add(enumerator.Current.Id);
                    }

                    return taken;
                });

            await Task.WhenAny(rest, Task.Delay(Patience));
            release.TrySetResult();

            returned.AddRange(await rest.WaitAsync(Bound));
            await growing.WaitAsync(Bound);

            Assert.Equal(returned.Count, returned.Distinct().Count());
            Assert.Equal(Enumerable.Range(0, full).Select(key => (long)key), returned.Where(key => key != full).Order());
        }
        finally
        {
            release.TrySetResult();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // How many of the keys 0, 1, 2, ... a database holds when the insert of the next one makes the
    // main table of its index grow for the given time. The switch of the growth counts the growths:
    // only the growth of the main table runs it, while the bytes of the index change as well when its
    // overflow takes a page. The switch runs inside the insert that grows the index, so it has run
    // when the insert has returned, and it is armed again for the next growth.
    private static async Task<int> KeysBeforeGrowthAsync(int growth)
    {
        await using var database = await ClashSchema.OpenAsync();
        var clashes = database.Set<ClashSchema.Clash>();
        var growths = 0;

        database.HoldNextGrowth(clashes.EntityType, Grew);

        for (var key = 0; key < MostKeys; key++)
        {
            var before = growths;

            await clashes.InsertAsync(ClashSchema.Of(key));

            if (growths == before)
            {
                continue;
            }

            if (growths == growth)
            {
                return key;
            }

            database.HoldNextGrowth(clashes.EntityType, Grew);
        }

        Assert.Fail($"The main table of the index did not grow {growth} times in {MostKeys} inserts.");

        return 0;

        void Grew() => growths++;
    }
}
