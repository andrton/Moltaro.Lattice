namespace Moltaro.Lattice.Tests.Database;

// WaitForPositionAsync, with a position and with a session, delivers its failures by the rule of every
// asynchronous member: it throws at the call only what the caller can know from its own arguments, and
// everything that depends on the state of the database comes through the task it returns, also on a path
// that needs no wait, and a close as well (AGENTS.md, "Shape of the public API": ArgumentException reports
// a caller's mistake, LatticeException with its code what the database decided):
// - thrown by the call: the default of LatticePosition, which is no position, a timeout that is not finite
//   and positive, and no session;
// - through the task: SynchronizationTimeout once a wait for a position ahead of the master has run out;
//   Unavailable before the open; HistoryMismatch for a position of another database; and
//   ObjectDisposedException after a close, each of the last three without a wait.
// Unavailable once the database faulted and the loss of the master of a child are brought about by the
// tests of the engine (Engine.Positions.WaitForPositionDeliveryTests), which can fault an instance and tell
// when a child has stopped following.
public sealed class WaitForPositionDeliveryTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Caller_mistakes_are_thrown_when_WaitForPositionAsync_is_called()
    {
        await using var database = await TestDatabases.OpenMasterAsync();

        Assert.Throws<ArgumentException>(() => { _ = database.WaitForPositionAsync(default(LatticePosition)); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = database.WaitForPositionAsync(database.Position, Timeout.InfiniteTimeSpan); });
        Assert.Throws<ArgumentNullException>(() => { _ = database.WaitForPositionAsync((LatticeSession)null!); });
    }

    [Fact]
    public async Task Timeout_of_a_wait_for_a_position_ahead_comes_through_the_task_WaitForPositionAsync_returns()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var ahead = TestData.Ahead(database.Position);

        var withPosition = database.WaitForPositionAsync(ahead, Short);
        var withSession = database.WaitForPositionAsync(new LatticeSession(ahead) { SynchronizationTimeout = Short });

        await ExpectThroughTheTaskAsync(withPosition, LatticeErrorEnum.SynchronizationTimeout);
        await ExpectThroughTheTaskAsync(withSession, LatticeErrorEnum.SynchronizationTimeout);
    }

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task Unavailable_before_the_open_comes_through_the_task_WaitForPositionAsync_returns()
    {
        await using var other = await TestDatabases.OpenMasterAsync();
        await using var database = TestDatabases.Create(new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = TestData.Schema });

        var withPosition = database.WaitForPositionAsync(other.Position);
        var withSession = database.WaitForPositionAsync(new LatticeSession());

        await ExpectThroughTheTaskAsync(withPosition, LatticeErrorEnum.Unavailable);
        await ExpectThroughTheTaskAsync(withSession, LatticeErrorEnum.Unavailable);
    }

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task HistoryMismatch_for_a_position_of_another_database_comes_through_the_task_WaitForPositionAsync_returns()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        await using var other = await TestDatabases.OpenMasterAsync();
        var foreign = (await other.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" })).Position;

        var withPosition = database.WaitForPositionAsync(foreign);
        var withSession = database.WaitForPositionAsync(new LatticeSession(foreign));

        await ExpectThroughTheTaskAsync(withPosition, LatticeErrorEnum.HistoryMismatch);
        await ExpectThroughTheTaskAsync(withSession, LatticeErrorEnum.HistoryMismatch);
    }

    [Fact(Skip = SkipReason.BeforeSprint14Part2)]
    public async Task Wait_after_a_close_is_ObjectDisposedException_through_the_task_WaitForPositionAsync_returns()
    {
        var database = await TestDatabases.OpenMasterAsync();
        var position = database.Position;

        await database.CloseAsync();

        var withPosition = database.WaitForPositionAsync(position);
        var withSession = database.WaitForPositionAsync(new LatticeSession(position));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => withPosition.AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => withSession.AsTask());
    }

    private static async Task ExpectThroughTheTaskAsync(ValueTask wait, LatticeErrorEnum error)
    {
        var refused = await Assert.ThrowsAsync<LatticeException>(() => wait.AsTask());

        Assert.Equal(error, refused.Error);
    }
}
