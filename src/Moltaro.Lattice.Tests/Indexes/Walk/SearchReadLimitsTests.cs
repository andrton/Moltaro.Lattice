using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Tests.Indexes.Walk;

// The read limits of the options for a search through an index, as for an enumeration
// (LatticeDatabaseOptions.ReadMemoryLimit and ReadTimeLimit; docs/decisions/queries.md, Q07.1.1):
// - ReadMemoryLimit counts what a search holds: the keys of the rows it has returned or passed over, which
//   it remembers so that it returns none twice, and, until FindAsync returns, its result, the list by its
//   capacity and each entity by the bytes stored for it; the lazy Find counts no entity it returned. A
//   search that would exceed the limit fails with LatticeException, ReadLimitExceeded and Limit
//   ReadMemory. What the lazy Find returned before stays valid, and every further step fails with the
//   same error, so it never ends as if it were complete.
// - An answer of one entity whose step saw the end is not counted against ReadMemoryLimit, as a read by
//   key is not; from the first step that returns an entity without seeing the end, everything the answer
//   holds counts, the first entity included. Under a limit smaller than one entity, a search that finds
//   exactly one succeeds, and one that finds two fails with the error of the limit.
// - ReadTimeLimit counts the time of the steps inside the engine, not the time between two of them.
// - A cancellation is reported before a limit: a search under a limit it would exceed, given a token
//   that is cancelled, ends in OperationCanceledException.
// The contract states no count of bytes, so the tests assert none. They choose sizes that any measure
// of the remembered keys and of the result tells apart: 20,000 Int64 keys take at least 160,000 bytes,
// which 64 KiB cannot hold, and 2,000 rows with a text of 200 characters take more in a result than
// 64 KiB, while 2,000 of their Int64 keys, 16,000 bytes and the room a set of them takes, fit in it.
// That a step which spends longer inside the engine than the time limit fails the search with ReadTime
// is stated by the tests of the engine, which can keep a step waiting inside it
// (SearchTimeInsideTheEngineTests in Moltaro.Lattice.Engine.Tests).
public sealed class SearchReadLimitsTests
{
    private const long Limit = 64 * 1024;

    [Fact]
    public async Task Walk_that_would_remember_more_than_the_memory_limit_fails_with_ReadMemory_and_every_further_step_fails_alike()
    {
        await using var database = await WalkSchema.OpenAsync("Utf8", readMemoryLimit: Limit);
        var rows = database.Set<Row>();

        for (var id = 1L; id <= 20_000; id++)
        {
            await rows.InsertAsync(WalkRows.Of(id, a: 1, c: id));
        }

        var byABC = rows.Index("ByABC");

        using var walk = byABC.Find(IndexKey.Equal<int?>(1)).GetEnumerator();
        var returned = new List<long>();
        LatticeException? failure = null;

        try
        {
            while (walk.MoveNext())
            {
                returned.Add(walk.Current.Id);
            }
        }
        catch (LatticeException exception)
        {
            failure = exception;
        }

        ExpectReadMemory(failure);
        Assert.NotEmpty(returned);
        Assert.Equal(returned.Count, returned.Distinct().Count());
        ExpectReadMemory(Record.Exception(() => walk.MoveNext()));

        var async = await Record.ExceptionAsync(async () => await byABC.FindAsync(IndexKey.Equal<int?>(1), new LatticeFindOptions { Reverse = true }));

        ExpectReadMemory(async);
    }

    [Fact]
    public async Task FindAsync_counts_its_result_against_the_memory_limit_and_the_lazy_Find_counts_no_entity_it_returned()
    {
        await using var database = await WalkSchema.OpenAsync("Utf8", readMemoryLimit: Limit);
        var rows = database.Set<Row>();
        var note = new string('n', 200);

        for (var id = 1L; id <= 2_000; id++)
        {
            await rows.InsertAsync(WalkRows.Of(id, a: 1, d: note));
        }

        var byA = rows.Index("ByA");

        Assert.Equal(2_000, byA.Find(IndexKey.Equal<int?>(1)).Count());
        ExpectReadMemory(await Record.ExceptionAsync(async () => await byA.FindAsync(IndexKey.Equal<int?>(1), new LatticeFindOptions())));
        ExpectReadMemory(await Record.ExceptionAsync(async () => await byA.FindAsync(IndexKey.All.Range(IndexBound.Inclusive(0), null), new LatticeFindOptions())));
    }

    [Fact]
    public async Task Answer_of_one_entity_is_not_counted_against_the_memory_limit_and_an_answer_of_two_is()
    {
        // A limit smaller than one entity.
        await using var database = await WalkSchema.OpenAsync("Utf8", readMemoryLimit: 16);
        var rows = database.Set<Row>();

        await rows.InsertAsync(WalkRows.Of(1, a: 1, c: 1));
        await rows.InsertAsync(WalkRows.Of(2, a: 2, c: 2));
        await rows.InsertAsync(WalkRows.Of(3, a: 2, c: 3));

        var byA = rows.Index("ByA");
        var byABC = rows.Index("ByABC");

        // One entity: by its full key, by a leading part, by a range, in both forms.
        Assert.Equal(1L, Assert.Single(byA.Find(IndexKey.Equal<int?>(1))).Id);
        Assert.Equal(1L, Assert.Single((await byA.FindAsync(IndexKey.Equal<int?>(1), new LatticeFindOptions())).Items).Id);
        Assert.Equal(1L, Assert.Single((await byABC.FindAsync(IndexKey.Equal<int?>(1), new LatticeFindOptions { Reverse = true })).Items).Id);
        Assert.Equal(1L, Assert.Single((await byA.FindAsync(IndexKey.All.Range(IndexBound.Inclusive(0), IndexBound.Exclusive(2)), new LatticeFindOptions())).Items).Id);

        // Two entities.
        ExpectReadMemory(Record.Exception(() => byA.Find(IndexKey.Equal<int?>(2)).ToList()));
        ExpectReadMemory(await Record.ExceptionAsync(async () => await byA.FindAsync(IndexKey.Equal<int?>(2), new LatticeFindOptions())));
        ExpectReadMemory(await Record.ExceptionAsync(async () => await byABC.FindAsync(IndexKey.All.Range(IndexBound.Inclusive(2), null), new LatticeFindOptions { Reverse = true })));
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Search_given_a_cancelled_token_under_a_limit_it_would_exceed_ends_in_cancellation_not_in_the_limit()
    {
        await using var database = await WalkSchema.OpenAsync("Utf8", readMemoryLimit: 16);
        var rows = database.Set<Row>();

        for (var id = 1L; id <= 100; id++)
        {
            await rows.InsertAsync(WalkRows.Of(id, a: 1, c: id));
        }

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var cancelled = await Record.ExceptionAsync(async () =>
            await rows.Index("ByABC").FindAsync(IndexKey.Equal<int?>(1), new LatticeFindOptions { Offset = 10, Limit = 50 }, cancellation.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(cancelled);
    }

    [Fact]
    public async Task Caller_that_is_slow_between_two_steps_of_a_walk_does_not_use_up_the_time_limit()
    {
        await using var database = await WalkSchema.OpenAsync("Utf8", readTimeLimit: ReadTimes.Limit);
        var rows = database.Set<Row>();

        for (var id = 1L; id <= 3; id++)
        {
            await rows.InsertAsync(WalkRows.Of(id, a: 1, c: id));
        }

        // A first walk, so that the walk the test times does not include compiling the code of a step.
        Assert.Equal(3, rows.Index("ByABC").Find(IndexKey.Equal<int?>(1)).Count());

        using var walk = rows.Index("ByABC").Find(IndexKey.Equal<int?>(1)).GetEnumerator();

        Assert.True(walk.MoveNext());

        await Task.Delay(ReadTimes.BeyondTheLimit);

        Assert.True(walk.MoveNext());
        Assert.True(walk.MoveNext());
        Assert.False(walk.MoveNext());
    }

    private static void ExpectReadMemory(Exception? failure)
    {
        var exception = Assert.IsType<LatticeException>(failure);

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, exception.Error);
        Assert.Equal(LatticeLimitEnum.ReadMemory, exception.Limit);
    }
}
