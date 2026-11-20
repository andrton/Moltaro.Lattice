namespace Moltaro.Lattice.Tests.Indexes;

// FindAsync delivers its failures by the rule of every asynchronous member: it throws at the call only what
// the caller can know from its own arguments, and everything that depends on the state of the database
// comes through the task it returns, also on a path that needs no wait, and a close as well, since a close
// can come between the call and its first step (AGENTS.md, "Shape of the public API": ArgumentException
// reports a caller's mistake, LatticeException with its code what the database decided). Each through a
// class and by name:
// - thrown by the call: no options, a key the index cannot serve, the default of IndexKey, which is no key;
// - through the task: SynchronizationTimeout once a wait for a position ahead of the master has run out,
//   HistoryMismatch for a session of another database, which needs no wait, and ObjectDisposedException
//   for a search after the database was closed.
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
    }

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task Search_after_a_close_is_ObjectDisposedException_through_the_task_FindAsync_returns()
    {
        var database = await MemberSchema.OpenAsync();
        var byClass = database.Set<Member>().Index("ByGroup");
        var byName = database.Set("Member").Index("ByGroup");

        await database.CloseAsync();

        var throughClass = byClass.FindAsync(IndexKey.Equal(1), Options);
        var throughName = byName.FindAsync(IndexKey.Equal(1), Options);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => throughClass.AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => throughName.AsTask());
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
