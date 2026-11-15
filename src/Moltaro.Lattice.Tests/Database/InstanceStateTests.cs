namespace Moltaro.Lattice.Tests.Database;

// The states of an instance of a MemoryOnly master and what an operation does in each (brief of
// Epic 1, sprint S06, "States"): Created until the open, Ready after it, Closed after a close.
// Before the open an operation is Unavailable; after a close it is ObjectDisposedException, also
// through a set that was obtained before. Closing a closed database does nothing, and disposing
// closes it in the same way.
public sealed class InstanceStateTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Before_the_open_every_operation_is_Unavailable(DatabaseImplementationEnum implementation)
    {
        await using var other = await TestDatabases.OpenMasterAsync(implementation);
        var position = other.Position;
        await using var database = TestDatabases.Create(implementation, MemoryOnly());

        Assert.Equal(LatticeDatabaseStateEnum.Created, database.State);

        TestData.ExpectError(LatticeErrorEnum.Unavailable, () => _ = database.Schema);
        TestData.ExpectError(LatticeErrorEnum.Unavailable, () => _ = database.Position);
        TestData.ExpectError(LatticeErrorEnum.Unavailable, () => database.Set<User>());
        TestData.ExpectError(LatticeErrorEnum.Unavailable, () => database.Set("User"));
        TestData.ExpectError(LatticeErrorEnum.Unavailable, () => database.CreateWriteBatch());
        await ExpectUnavailableAsync(() => database.WaitForPositionAsync(position));
        await ExpectUnavailableAsync(() => database.WaitForPositionAsync(new LatticeSession()));
        await ExpectUnavailableAsync(() => database.WaitForPositionAsync(new LatticeSession(position)));

        Assert.Equal(LatticeDatabaseStateEnum.Created, database.State);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task After_a_close_every_operation_is_ObjectDisposedException(DatabaseImplementationEnum implementation)
    {
        var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        Assert.True(users.TryGet(1, out var user, out var revision));
        Assert.True(userRecords.TryGet(1L, out var record));
        var position = database.Position;

        await database.CloseAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);

        // The database itself.
        Assert.Throws<ObjectDisposedException>(() => database.Schema);
        Assert.Throws<ObjectDisposedException>(() => database.Position);
        Assert.Throws<ObjectDisposedException>(() => database.Set<User>());
        Assert.Throws<ObjectDisposedException>(() => database.Set("User"));
        Assert.Throws<ObjectDisposedException>(() => database.CreateWriteBatch());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await database.WaitForPositionAsync(position));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await database.WaitForPositionAsync(new LatticeSession(position)));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await database.OpenAsync());

        // Through a class, with a set obtained before the close.
        Assert.Throws<ObjectDisposedException>(() => users.Get(1));
        Assert.Throws<ObjectDisposedException>(() => users.TryGet(1, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => users.Enumerate().ToList());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await users.GetAsync(1, new LatticeSession()));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await users.EnumerateAsync(new LatticeSession()).ToListAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await users.InsertAsync(new User { Id = 2, Name = "Grace" }));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await users.ReplaceAsync(user, revision));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await users.UpsertAsync(user));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await users.DeleteAsync(1));

        // By name, likewise.
        Assert.Throws<ObjectDisposedException>(() => userRecords.Get(1L));
        Assert.Throws<ObjectDisposedException>(() => userRecords.Enumerate().ToList());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await userRecords.GetAsync(1L, new LatticeSession()));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await userRecords.ReplaceAsync(record));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await userRecords.DeleteAsync(1L));

        // What was read before stays the caller's.
        Assert.Equal("Ada", user.Name);
        Assert.Equal("Ada", record["Name"]);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Closing_or_disposing_a_closed_database_does_nothing(DatabaseImplementationEnum implementation)
    {
        var closed = await TestDatabases.OpenMasterAsync(implementation);
        await closed.CloseAsync();
        await closed.CloseAsync();
        await closed.DisposeAsync();
        closed.Dispose();

        Assert.Equal(LatticeDatabaseStateEnum.Closed, closed.State);

        // Disposing an open database closes it.
        var disposed = await TestDatabases.OpenMasterAsync(implementation);
        disposed.Dispose();

        Assert.Equal(LatticeDatabaseStateEnum.Closed, disposed.State);
        Assert.Throws<ObjectDisposedException>(() => disposed.Set<User>());
    }

    private static LatticeDatabaseOptions MemoryOnly() =>
        new() { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = TestData.Schema };

    private static async Task ExpectUnavailableAsync(Func<ValueTask> wait)
    {
        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await wait());
        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
    }
}
