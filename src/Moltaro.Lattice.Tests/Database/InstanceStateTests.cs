using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Database;

// The states of an instance of a MemoryOnly master and what an operation does in each (brief of
// Epic 1, sprint S06, "States"): Created until the open, Ready after it, Closed after a close.
// Before the open an operation is Unavailable; after a close it is ObjectDisposedException, also
// through a set that was obtained before. Closing a closed database does nothing, and disposing
// closes it in the same way. The close is one operation (brief of S09, part 2, the first finding of
// the outside review it carries): a close or a dispose called while an earlier close has not
// completed completes with it, without an error, and a token that cancels the wait of a close does
// not stop the close. That a later call waits for the memory to be released is not visible here; the
// tests of the engine state it (OneCloseTests).
public sealed class InstanceStateTests
{
    [Fact]
    public async Task Before_the_open_every_operation_is_Unavailable()
    {
        await using var other = await TestDatabases.OpenMasterAsync();
        var position = other.Position;
        await using var database = TestDatabases.Create(MemoryOnly());

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

    [Fact]
    public async Task After_a_close_every_operation_is_ObjectDisposedException()
    {
        var database = await TestDatabases.OpenMasterAsync();
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

    [Fact]
    public async Task Closing_or_disposing_a_closed_database_does_nothing()
    {
        var closed = await TestDatabases.OpenMasterAsync();
        await closed.CloseAsync();
        await closed.CloseAsync();
        await closed.DisposeAsync();
        closed.Dispose();

        Assert.Equal(LatticeDatabaseStateEnum.Closed, closed.State);

        // Disposing an open database closes it.
        var disposed = await TestDatabases.OpenMasterAsync();
        disposed.Dispose();

        Assert.Equal(LatticeDatabaseStateEnum.Closed, disposed.State);
        Assert.Throws<ObjectDisposedException>(() => disposed.Set<User>());
    }

    [Fact]
    public async Task Close_and_dispose_called_before_an_earlier_close_has_completed_complete_with_it_without_an_error()
    {
        var database = await TestDatabases.OpenMasterAsync();
        await database.Users().InsertAsync(new User { Id = 1, Name = "Ada" });

        // None of the calls is awaited before the next one is made.
        var first = database.CloseAsync().AsTask();
        var second = database.CloseAsync().AsTask();
        var disposed = database.DisposeAsync().AsTask();

        await Task.WhenAll(first, second, disposed).WaitAsync(Bound);

        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
        Assert.Throws<ObjectDisposedException>(() => database.Set<User>());
    }

    [Fact]
    public async Task Close_whose_wait_is_cancelled_still_closes_the_database_and_a_later_close_and_dispose_complete_without_an_error()
    {
        var database = await TestDatabases.OpenMasterAsync();
        var users = database.Users();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // With nothing to wait for, the call may complete or report the cancellation of its wait; the
        // database closes either way.
        var failure = await Record.ExceptionAsync(() => database.CloseAsync(cancelled.Token).AsTask().WaitAsync(Bound));

        Assert.True(failure is null or OperationCanceledException, $"A close with a cancelled token failed with {failure?.GetType().Name}.");
        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
        Assert.Throws<ObjectDisposedException>(() => users.Get(1));

        await database.CloseAsync().AsTask().WaitAsync(Bound);
        await database.DisposeAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
    }

    [Fact]
    public async Task After_a_close_a_read_of_an_entity_type_the_engine_does_not_store_is_ObjectDisposedException()
    {
        // Holder has a List field: the engine does not store it.
        var schema = LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Holder", "ClrType": "{{typeof(Holder).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
        var database = await TestDatabases.OpenMasterAsync(schema);
        var holders = database.Set<Holder>();
        var holderRecords = database.Set("Holder");

        await database.CloseAsync();

        Assert.Throws<ObjectDisposedException>(() => holders.Get(1L));
        Assert.Throws<ObjectDisposedException>(() => holders.TryGet(1L, out _, out _));
        Assert.Throws<ObjectDisposedException>(() => holders.Enumerate().ToList());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await holders.GetAsync(1L, new LatticeSession()));
        Assert.Throws<ObjectDisposedException>(() => holderRecords.Get(1L));
        Assert.Throws<ObjectDisposedException>(() => holderRecords.Enumerate().ToList());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await holderRecords.EnumerateAsync(new LatticeSession()).ToListAsync());
    }

    private static LatticeDatabaseOptions MemoryOnly() =>
        new() { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = TestData.Schema };

    private static async Task ExpectUnavailableAsync(Func<ValueTask> wait)
    {
        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await wait());
        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
    }

    private sealed class Holder
    {
        public long Id { get; set; }

        public List<string> Lines { get; set; } = [];
    }
}
