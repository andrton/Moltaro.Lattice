namespace Moltaro.Lattice.Tests.Indexes.Walk;

// FindAsync of a walk, by leading parts or a range, in reverse or in pages, delivers its failures by the
// rule of every asynchronous member, as the search by the full key does (FindAsyncDeliveryTests; AGENTS.md,
// "Shape of the public API"): it throws at the call only what the caller can know from its own arguments,
// and everything that depends on the state of the database comes through the task it returns, also on a
// path that needs no wait. Each through a class and by name:
// - thrown by the call: a range on a part the key leaves no room for, a bound of another type than its
//   field, a bound that is the default of IndexBound, which has no value;
// - through the task: ObjectDisposedException for a walk after the database was closed; HistoryMismatch
//   for a session of another database; OperationCanceledException for a token cancelled before the
//   call; ReadLimitExceeded for a walk over the memory limit of a read.
public sealed class WalkDeliveryTests
{
    // A walk of every form: leading parts and a range, in reverse, after an Offset, with a Limit.
    private static readonly IndexKey Walk = IndexKey.Equal<int?>(1).Range(IndexBound.Inclusive("a"), null);

    private static readonly LatticeFindOptions Paged = new() { Reverse = true, Offset = 1, Limit = 2 };

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Caller_mistakes_in_the_range_are_thrown_when_FindAsync_is_called()
    {
        await using var database = await WalkSchema.OpenAsync("Utf8");

        foreach (var index in IndexesOf(database, "ByAB"))
        {
            Assert.Throws<ArgumentException>(() => { _ = index(IndexKey.Equal<int?, string?>(1, "a").Range(IndexBound.Inclusive("b"), null), Paged, default); });
            Assert.Throws<ArgumentException>(() => { _ = index(IndexKey.Equal<int?>(1).Range(IndexBound.Inclusive(2), null), Paged, default); });
            Assert.Throws<ArgumentException>(() => { _ = index(IndexKey.Equal<int?>(1).Range(default(IndexBound), null), Paged, default); });
            Assert.Throws<ArgumentNullException>(() => { _ = index(Walk, null!, default); });
        }
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task What_the_database_decides_about_a_walk_comes_through_the_task_FindAsync_returns()
    {
        await using var other = await WalkSchema.OpenAsync("Utf8");
        var foreign = (await other.Set<Row>().InsertAsync(WalkRows.Of(1, a: 1, b: "a"))).Position;

        await using var limited = await WalkSchema.OpenAsync("Utf8", readMemoryLimit: 16);

        for (var id = 1L; id <= 100; id++)
        {
            await limited.Set<Row>().InsertAsync(WalkRows.Of(id, a: 1, b: "b"));
        }

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        foreach (var index in IndexesOf(limited, "ByAB"))
        {
            var ofAnotherHistory = index(Walk, new LatticeFindOptions { Session = new LatticeSession(foreign), Reverse = true, Offset = 1, Limit = 2 }, default);
            var cancelled = index(Walk, Paged, cancellation.Token);
            var overTheLimit = index(Walk, new LatticeFindOptions { Reverse = true }, default);

            Assert.Equal(LatticeErrorEnum.HistoryMismatch, (await Assert.ThrowsAsync<LatticeException>(() => ofAnotherHistory)).Error);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
            Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, (await Assert.ThrowsAsync<LatticeException>(() => overTheLimit)).Error);
        }

        var closing = await WalkSchema.OpenAsync("Utf8");
        var afterClose = IndexesOf(closing, "ByAB");

        await closing.CloseAsync();

        foreach (var index in afterClose)
        {
            var closed = index(Walk, Paged, default);

            await Assert.ThrowsAsync<ObjectDisposedException>(() => closed);
        }
    }

    // FindAsync of the index through the class and by name, each giving the rows it found as a task.
    private static List<Func<IndexKey, LatticeFindOptions, CancellationToken, Task>> IndexesOf(ILatticeDatabase database, string name)
    {
        var byClass = database.Set<Row>().Index(name);
        var byName = database.Set("Row").Index(name);

        return
        [
            (key, options, token) => byClass.FindAsync(key, options, token).AsTask(),
            (key, options, token) => byName.FindAsync(key, options, token).AsTask(),
        ];
    }
}
