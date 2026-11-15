namespace Moltaro.Lattice.Tests.Database;

public sealed class OpeningAndClosingTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Opened_database_is_ready_and_closed_database_is_closed(DatabaseImplementationEnum implementation)
    {
        var database = await TestDatabases.OpenMasterAsync(implementation);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);

        await database.CloseAsync();
        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);

        // Closing again does nothing.
        await database.CloseAsync();
        await database.DisposeAsync();
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Database_can_be_created_first_and_opened_later(DatabaseImplementationEnum implementation)
    {
        // The object exists and can be handed around before the database is open.
        await using var database = TestDatabases.Create(implementation, new LatticeDatabaseOptions
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

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Database_closed_before_opening_cannot_be_opened(DatabaseImplementationEnum implementation)
    {
        var database = TestDatabases.Create(implementation, new LatticeDatabaseOptions
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
        var child = LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = MasterAndChild.NewAddress(),
        });

        var opening = child.OpenAsync().AsTask();
        await Task.Delay(50);
        Assert.Equal(LatticeDatabaseStateEnum.Connecting, child.State);

        await child.CloseAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Closed_database_refuses_reads_and_writes_but_returned_entities_stay_valid(DatabaseImplementationEnum implementation)
    {
        var database = await TestDatabases.OpenMasterAsync(implementation);
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

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Memory_only_database_starts_empty_every_time(DatabaseImplementationEnum implementation)
    {
        var first = await TestDatabases.OpenMasterAsync(implementation);
        await first.Users().InsertAsync(new User { Id = 1, Name = "Ada" });
        await first.CloseAsync();

        await using var second = await TestDatabases.OpenMasterAsync(implementation);

        Assert.Empty(second.Users().Enumerate());
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Durable_persistence_is_not_implemented_yet(DatabaseImplementationEnum implementation)
    {
        var options = new LatticeDatabaseOptions { StorageConnectionString = "Host=localhost" };

        await Assert.ThrowsAsync<NotSupportedException>(async () => await TestDatabases.OpenAsync(implementation, options));
    }
}
