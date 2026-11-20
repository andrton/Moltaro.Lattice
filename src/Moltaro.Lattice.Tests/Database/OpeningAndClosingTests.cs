namespace Moltaro.Lattice.Tests.Database;

public sealed class OpeningAndClosingTests
{
    [Fact]
    public async Task Opened_database_is_ready_and_closed_database_is_closed()
    {
        var database = await TestDatabases.OpenMasterAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);

        await database.CloseAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);

        // Closing again does nothing.
        await database.CloseAsync();
        await database.DisposeAsync();
    }

    [Fact]
    public async Task Database_can_be_created_first_and_opened_later()
    {
        // The object exists and can be handed around before the database is open.
        await using var database = TestDatabases.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
        });
        Assert.Equal(LatticeDatabaseStateEnum.Created, database.State);
        TestData.ExpectError(LatticeErrorEnum.Unavailable, () => database.Set<User>());

        await database.OpenAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });

        // An instance is opened once.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await database.OpenAsync());
    }

    [Fact]
    public async Task Database_closed_before_opening_cannot_be_opened()
    {
        var database = TestDatabases.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
        });

        await database.CloseAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await database.OpenAsync());
    }

    [Fact]
    public async Task Closing_a_child_ends_its_wait_for_the_master()
    {
        using var port = new ListeningPort();

        var child = LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = port.Address,
        });

        var opening = child.OpenAsync().AsTask();
        await port.EndAttemptAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Connecting, child.State);

        await child.CloseAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
    }

    [Fact]
    public async Task Closed_database_refuses_reads_and_writes_but_returned_entities_stay_valid()
    {
        var database = await TestDatabases.OpenMasterAsync();
        var users = database.Users();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        Assert.True(users.TryGet(1, out var user));

        await database.CloseAsync();

        Assert.Equal("Ada", user.Name);
        Assert.Throws<ObjectDisposedException>(() => users.TryGet(1, out _));
        Assert.Throws<ObjectDisposedException>(() => users.Enumerate().ToList());
        Assert.Throws<ObjectDisposedException>(() => database.Users());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await users.InsertAsync(new User { Id = 2 }));
    }

    [Fact]
    public async Task Memory_only_database_starts_empty_every_time()
    {
        var first = await TestDatabases.OpenMasterAsync();
        await first.Users().InsertAsync(new User { Id = 1, Name = "Ada" });
        await first.CloseAsync();

        await using var second = await TestDatabases.OpenMasterAsync();

        Assert.Empty(second.Users().Enumerate());
    }

    [Fact]
    public async Task Durable_persistence_is_not_implemented_yet()
    {
        var options = new LatticeDatabaseOptions { StorageConnectionString = "Host=localhost" };

        await Assert.ThrowsAsync<NotSupportedException>(async () => await TestDatabases.OpenAsync(options));
    }
}
