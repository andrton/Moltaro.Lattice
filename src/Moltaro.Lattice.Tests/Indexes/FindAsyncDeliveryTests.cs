namespace Moltaro.Lattice.Tests.Indexes;

// FindAsync delivers its failures by the rule of every asynchronous member: a caller's mistake is thrown
// when the method is called, and what the database decides comes through the task it returns, also when
// the search meets it on a path that needs no wait (AGENTS.md, "Shape of the public API": ArgumentException
// reports a caller's mistake, LatticeException with its code what the database decided). Each through a
// class and by name:
// - thrown by the call: no options, a key the index cannot serve, the default of IndexKey, which is no key,
//   and a search after the database was closed;
// - through the task: SynchronizationTimeout once a wait for a position ahead of the master has run out,
//   and HistoryMismatch for a session of another database, which needs no wait.
public sealed class FindAsyncDeliveryTests
{
    private static readonly LatticeFindOptions Options = new();

    [Fact]
    public async Task Caller_mistakes_are_thrown_when_FindAsync_is_called()
    {
        var database = await MemberSchema.OpenAsync();
        var byClass = database.Set<Member>().Index("ByGroup");
        var byName = database.Set("Member").Index("ByGroup");

        Assert.Throws<ArgumentNullException>(() => { _ = byClass.FindAsync(IndexKey.Equal(1), null!); });
        Assert.Throws<ArgumentNullException>(() => { _ = byName.FindAsync(IndexKey.Equal(1), null!); });
        Assert.Throws<ArgumentException>(() => { _ = byClass.FindAsync(IndexKey.Equal("one"), Options); });
        Assert.Throws<ArgumentException>(() => { _ = byName.FindAsync(IndexKey.Equal("one"), Options); });
        Assert.Throws<ArgumentException>(() => { _ = byClass.FindAsync(default, Options); });
        Assert.Throws<ArgumentException>(() => { _ = byName.FindAsync(default, Options); });

        await database.CloseAsync();

        Assert.Throws<ObjectDisposedException>(() => { _ = byClass.FindAsync(IndexKey.Equal(1), Options); });
        Assert.Throws<ObjectDisposedException>(() => { _ = byName.FindAsync(IndexKey.Equal(1), Options); });
    }

    [Fact]
    public async Task Timeout_of_the_wait_for_the_position_of_the_session_comes_through_the_task_FindAsync_returns()
    {
        await using var database = await MemberSchema.OpenAsync();
        var written = await database.Set<Member>().InsertAsync(new Member { Id = 1, Group = 1, Login = "login 1" });
        var ahead = new LatticeFindOptions { Session = new LatticeSession(TestData.Ahead(written.Position)) { SynchronizationTimeout = TimeSpan.FromMilliseconds(50) } };

        var byClass = database.Set<Member>().Index("ByGroup").FindAsync(IndexKey.Equal(1), ahead);
        var byName = database.Set("Member").Index("ByGroup").FindAsync(IndexKey.Equal(1), ahead);

        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, (await Assert.ThrowsAsync<LatticeException>(() => byClass.AsTask())).Error);
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, (await Assert.ThrowsAsync<LatticeException>(() => byName.AsTask())).Error);
    }

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task Refusal_the_database_decides_without_a_wait_comes_through_the_task_FindAsync_returns()
    {
        await using var database = await MemberSchema.OpenAsync();
        await using var other = await MemberSchema.OpenAsync();
        var foreign = (await other.Set<Member>().InsertAsync(new Member { Id = 1, Group = 1, Login = "login 1" })).Position;
        var ofAnotherHistory = new LatticeFindOptions { Session = new LatticeSession(foreign) };

        var byClass = database.Set<Member>().Index("ByGroup").FindAsync(IndexKey.Equal(1), ofAnotherHistory);
        var byName = database.Set("Member").Index("ByGroup").FindAsync(IndexKey.Equal(1), ofAnotherHistory);

        Assert.Equal(LatticeErrorEnum.HistoryMismatch, (await Assert.ThrowsAsync<LatticeException>(() => byClass.AsTask())).Error);
        Assert.Equal(LatticeErrorEnum.HistoryMismatch, (await Assert.ThrowsAsync<LatticeException>(() => byName.AsTask())).Error);
    }
}
