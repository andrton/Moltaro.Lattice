using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.TestSupport.Enumeration;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// The time limit of a read, LatticeDatabaseOptions.ReadTimeLimit, counts the time inside the steps of a
// search through an index as it does for an enumeration, a wait inside the engine included
// (ReadTimeInsideTheEngineTests): a search whose steps spend longer inside the engine than the limit fails
// with LatticeException, ReadLimitExceeded and Limit ReadTime, and every further step of the lazy Find
// fails the same way. And a cancellation is reported before a limit: a FindAsync whose step waits inside
// the engine past the limit, and whose token is then cancelled, ends in OperationCanceledException.
//
// The test keeps a step inside the engine for as long as it likes, as ReadTimeInsideTheEngineTests does: it
// holds every slot of the epoch table with reads by key (HeldRead), so that the next step of a search waits
// for a slot, and lets one go once the step has waited longer than the limit. The engine checks the limit
// at the points of a step where it can stop safely, so the step that waited may still return its entity:
// the search then fails at its next step. The limit is that of ReadTimes, which the first steps, which do
// not wait, reach only if the machine holds them up for two seconds. Before it counts, the test takes the
// same searches on a database without a limit, so that the first step it times does not include compiling
// the code of a step. Every wait is bounded.
public sealed class SearchTimeInsideTheEngineTests
{
    private const int Rows = 4;

    private static readonly IndexKey Walk = IndexKey.All.Range(IndexBound.Inclusive(0L), null);

    [Fact]
    public async Task Search_whose_step_waits_inside_the_engine_longer_than_the_time_limit_fails_with_ReadTime()
    {
        await using (var warm = await OpenWithRowsAsync(readTimeLimit: null))
        {
            var byNumber = warm.Set<IndexSchema.Counted>().Index("ByNumber");

            Assert.Equal(Rows, byNumber.Find(Walk).Count());
            Assert.Equal(Rows, (await byNumber.FindAsync(Walk, new LatticeFindOptions())).Items.Count);
        }

        var database = await OpenWithRowsAsync(ReadTimes.Limit);
        var held = new List<HeldRead>();

        try
        {
            var byNumber = database.Set<IndexSchema.Counted>().Index("ByNumber");
            using var walk = byNumber.Find(Walk).GetEnumerator();

            Assert.True(walk.MoveNext());

            await HoldEverySlotAsync(database, held);

            using var started = new CountdownEvent(2);
            var lazy = OwnThread.Run(() =>
            {
                started.Signal();

                return Record.Exception(() =>
                {
                    while (walk.MoveNext())
                    {
                    }
                });
            });
            var whole = OwnThread.Run(() =>
            {
                started.Signal();

                return Record.Exception(() => OwnThread.Wait(byNumber.FindAsync(Walk, new LatticeFindOptions())));
            });

            Assert.True(started.Wait(Bound), "The searches did not start.");
            await Task.Delay(ReadTimes.BeyondTheLimit);
            Assert.False(lazy.IsCompleted || whole.IsCompleted, "A step ended while every slot of the epoch table was held.");

            await held[0].ReleaseAsync();

            ExpectReadTime(await lazy.WaitAsync(Bound));
            ExpectReadTime(await whole.WaitAsync(Bound));
            ExpectReadTime(Record.Exception(() => walk.MoveNext()));
        }
        finally
        {
            foreach (var read in held)
            {
                read.Dispose();
            }

            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task FindAsync_cancelled_while_its_step_waits_inside_the_engine_past_the_time_limit_ends_in_cancellation()
    {
        var database = await OpenWithRowsAsync(ReadTimes.Limit);
        var held = new List<HeldRead>();

        try
        {
            var byNumber = database.Set<IndexSchema.Counted>().Index("ByNumber");
            using var cancellation = new CancellationTokenSource();

            await HoldEverySlotAsync(database, held);

            // On a thread of its own: a search without a session takes its steps inside the call, which
            // waits there for a slot.
            var search = OwnThread.Run(() => OwnThread.Wait(byNumber.FindAsync(Walk, new LatticeFindOptions { Reverse = true, Limit = 2 }, cancellation.Token)));

            await Task.Delay(ReadTimes.BeyondTheLimit);
            Assert.False(search.IsCompleted, "The search ended while every slot of the epoch table was held.");

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search.WaitAsync(Bound));
        }
        finally
        {
            foreach (var read in held)
            {
                read.Dispose();
            }

            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // Every slot of the epoch table held by a read by key of a row.
    private static async Task HoldEverySlotAsync(EngineDatabase database, List<HeldRead> held)
    {
        var slots = new ReadEpochs(new object()).SlotCount;
        var counted = database.Set<IndexSchema.Counted>();

        for (var read = 0; read < slots; read++)
        {
            var key = (long)((read % Rows) + 1);

            held.Add(await HeldRead.StartAsync(database, () => counted.Get(key)?.Number));
        }
    }

    // A master of the engine with the rows 1 to 4 of Counted, of the Number of their id, under the time
    // limit given in its options.
    private static async Task<EngineDatabase> OpenWithRowsAsync(TimeSpan? readTimeLimit)
    {
        var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = IndexSchema.Utf8,
            ReadTimeLimit = readTimeLimit,
        });

        await database.OpenAsync();

        for (var id = 1L; id <= Rows; id++)
        {
            await database.Set<IndexSchema.Counted>().InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
        }

        return database;
    }

    private static void ExpectReadTime(Exception? failure)
    {
        var exception = Assert.IsType<LatticeException>(failure);

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, exception.Error);
        Assert.Equal(LatticeLimitEnum.ReadTime, exception.Limit);
    }
}
