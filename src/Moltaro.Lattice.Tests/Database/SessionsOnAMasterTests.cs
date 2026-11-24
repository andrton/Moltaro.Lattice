namespace Moltaro.Lattice.Tests.Database;

// A master has already applied the positions it gives out and can serve a wait for any of them
// at once (docs/decisions/replication-and-hosting.md, "Read consistency boundaries"). A write
// moves the session to the position of the write, and a read moves it to the position of its
// answer. Waiting for a position from another database history is HistoryMismatch.
//
// A position of the history of the database that is ahead of it, which only a caller can make up, is
// what WaitForPositionAsync describes: the wait lasts until the instance has applied the position,
// and ends with SynchronizationTimeout when that does not happen in time. A read with a session that
// depends on such a position, by key and in an enumeration, waits the same way, for the timeout of
// the options of the database unless the session sets its own, and answers once a write reaches the
// position; an enumeration that timed out returned nothing. A test makes one up from
// the text of a position (TestData.Ahead). A read that takes a session and is given none throws at the
// call, through a class and by name: it is a mistake the caller can know from its own arguments.
public sealed class SessionsOnAMasterTests
{
    [Fact]
    public async Task Every_write_with_a_session_moves_the_session_to_the_position_of_the_write()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();
        var settings = database.Set("Setting");
        var session = new LatticeSession();
        LatticeRecord Setting(string name) => new(settings.EntityType) { ["Name"] = name, ["Value"] = "v", ["Priority"] = 1 };

        // Through a class.
        await ExpectMovedAsync(users.InsertAsync(new User { Id = 1, Name = "Ada" }, session));
        await ExpectMovedAsync(users.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" }, session));
        Assert.True(users.TryGet(1, out var user, out var userRevision));
        await ExpectMovedAsync(users.ReplaceAsync(user, userRevision, session));
        await ExpectMovedAsync(users.UpsertAsync(new User { Id = 2, Name = "Grace" }, session));
        Assert.True(users.TryGet(2, out _, out var graceRevision));
        await ExpectMovedAsync(users.DeleteAsync(2, graceRevision, session));
        await ExpectMovedAsync(users.DeleteAsync(1, session));
        await ExpectMovedAsync(users.DeleteAsync(404, session));

        // By name.
        await ExpectMovedAsync(settings.InsertAsync(Setting("theme"), session));
        await ExpectMovedAsync(settings.ReplaceAsync(Setting("theme"), session));
        Assert.True(settings.TryGet("theme", out var setting, out var settingRevision));
        await ExpectMovedAsync(settings.ReplaceAsync(setting, settingRevision, session));
        await ExpectMovedAsync(settings.UpsertAsync(Setting("language"), session));
        Assert.True(settings.TryGet("language", out _, out var languageRevision));
        await ExpectMovedAsync(settings.DeleteAsync("language", languageRevision, session));
        await ExpectMovedAsync(settings.DeleteAsync("theme", session));
        await ExpectMovedAsync(settings.DeleteAsync("missing", session));

        async Task ExpectMovedAsync(ValueTask<LatticeWriteResult> write)
        {
            var result = await write;

            Assert.Equal(result.Position, session.MinPosition);
            Assert.Equal(result.Position, database.Position);
        }
    }

    [Fact]
    public async Task Every_read_with_a_session_moves_the_session_to_the_position_of_its_answer()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        var first = await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        await users.InsertAsync(new User { Id = 2, Name = "Grace" });

        // Sessions that depend on an earlier position, and one that depends on none.
        foreach (var session in new[] { new LatticeSession(first.Position), new LatticeSession() })
        {
            var found = await users.GetAsync(1, session);
            Assert.True(found.Found);
            Assert.Equal("Ada", found.Entity.Name);
            Assert.Equal(database.Position, found.Position);
            Assert.Equal(database.Position, session.MinPosition);
        }

        var missing = new LatticeSession(first.Position);
        var notFound = await userRecords.GetAsync(404L, missing);
        Assert.False(notFound.Found);
        Assert.Equal(database.Position, notFound.Position);
        Assert.Equal(database.Position, missing.MinPosition);

        var byClass = new LatticeSession(first.Position);
        Assert.Equal([1L, 2L], (await users.EnumerateAsync(byClass).ToListAsync()).Select(user => user.Id).Order());
        Assert.Equal(database.Position, byClass.MinPosition);

        var byName = new LatticeSession(first.Position);
        Assert.Equal([1L, 2L], (await userRecords.EnumerateAsync(byName).ToListAsync()).Select(record => record.Get<long>("Id")).Order());
        Assert.Equal(database.Position, byName.MinPosition);

        // Nothing to return is an answer too.
        var empty = new LatticeSession();
        Assert.Empty(await database.Set("Setting").EnumerateAsync(empty).ToListAsync());
        Assert.Equal(database.Position, empty.MinPosition);
    }

    [Fact]
    public async Task Read_that_takes_a_session_throws_at_the_call_when_none_is_given()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();
        var userRecords = database.Set("User");

        Assert.Throws<ArgumentNullException>(() => { _ = users.GetAsync(1, null!); });
        Assert.Throws<ArgumentNullException>(() => users.EnumerateAsync(null!));
        Assert.Throws<ArgumentNullException>(() => { _ = userRecords.GetAsync(1L, null!); });
        Assert.Throws<ArgumentNullException>(() => userRecords.EnumerateAsync(null!));
    }

    [Fact]
    public async Task Waiting_for_a_position_the_master_gave_out_completes_at_once()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();
        var start = database.Position;
        var first = await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        var second = await users.DeleteAsync(1);

        foreach (var position in new[] { start, first.Position, second.Position, database.Position })
        {
            ExpectCompletedAtOnce(database.WaitForPositionAsync(position));
            ExpectCompletedAtOnce(database.WaitForPositionAsync(position, TimeSpan.FromMilliseconds(1)));
            ExpectCompletedAtOnce(database.WaitForPositionAsync(new LatticeSession(position)));
        }

        ExpectCompletedAtOnce(database.WaitForPositionAsync(new LatticeSession()));

        static void ExpectCompletedAtOnce(ValueTask wait)
        {
            Assert.True(wait.IsCompletedSuccessfully);
            wait.GetAwaiter().GetResult();
        }
    }

    [Fact]
    public async Task Wait_for_a_position_ahead_of_the_master_lasts_until_a_write_reaches_it_or_ends_with_SynchronizationTimeout()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        var ahead = TestData.Ahead(database.Position);

        // No write reaches it: the wait ends by its timeout, however short.
        var timedOut = await Assert.ThrowsAsync<LatticeException>(async () => await database.WaitForPositionAsync(ahead, TimeSpan.FromMilliseconds(1)));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, timedOut.Error);

        // With a timeout long enough the wait lasts until the next write, which reaches it.
        var waiting = database.WaitForPositionAsync(ahead, TimeSpan.FromMinutes(1)).AsTask();
        Assert.False(waiting.IsCompleted);

        await users.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });

        await waiting;
    }

    [Fact]
    public async Task Read_with_a_session_ahead_of_the_master_lasts_until_a_write_reaches_its_position_or_ends_with_SynchronizationTimeout()
    {
        // The timeout of the options is short, so that a read whose session sets none ends soon.
        await using var database = await TestDatabases.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
            SynchronizationTimeout = TimeSpan.FromMilliseconds(1),
        });
        var users = database.Set<User>();
        var records = database.Set("User");
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        var ahead = TestData.Ahead(database.Position);

        // Reads whose session sets a timeout long enough: by key and in an enumeration, through the
        // class and by name.
        LatticeSession Patient() => new(ahead) { SynchronizationTimeout = TimeSpan.FromMinutes(1) };
        var byKey = users.GetAsync(1L, Patient()).AsTask();
        var byName = records.GetAsync(1L, Patient()).AsTask();
        var enumeration = users.EnumerateAsync(Patient()).ToListAsync().AsTask();
        var enumerationByName = records.EnumerateAsync(Patient()).ToListAsync().AsTask();

        // No write reaches the position yet: the same reads whose session sets no timeout end by the
        // timeout of the options, and the others are still waiting after them.
        await ExpectTimedOutAsync(async () => await users.GetAsync(1L, new LatticeSession(ahead)));
        await ExpectTimedOutAsync(async () => await records.GetAsync(1L, new LatticeSession(ahead)));
        Assert.Equal(0, await ItemsBeforeTheTimeoutAsync(users.EnumerateAsync(new LatticeSession(ahead))));
        Assert.Equal(0, await ItemsBeforeTheTimeoutAsync(records.EnumerateAsync(new LatticeSession(ahead))));

        Assert.False(byKey.IsCompleted);
        Assert.False(byName.IsCompleted);
        Assert.False(enumeration.IsCompleted);
        Assert.False(enumerationByName.IsCompleted);

        // The next write reaches the position, and each waiting read answers with what it left.
        await users.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });

        Assert.Equal("Ada Lovelace", (await byKey).Entity!.Name);
        Assert.Equal("Ada Lovelace", (await byName).Entity!["Name"]);
        Assert.Equal("Ada Lovelace", Assert.Single(await enumeration).Name);
        Assert.Equal("Ada Lovelace", Assert.Single(await enumerationByName)["Name"]);

        static async Task ExpectTimedOutAsync(Func<Task> read)
        {
            var exception = await Assert.ThrowsAsync<LatticeException>(read);

            Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, exception.Error);
        }

        // The items an enumeration returned before it ended with SynchronizationTimeout.
        static async Task<int> ItemsBeforeTheTimeoutAsync<T>(IAsyncEnumerable<T> items)
        {
            var count = 0;

            await ExpectTimedOutAsync(async () =>
            {
                await foreach (var item in items)
                {
                    count++;
                }
            });

            return count;
        }
    }

    [Fact]
    public async Task Position_of_another_database_history_is_HistoryMismatch()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        await using var other = await TestDatabases.OpenMasterAsync();
        var foreign = (await other.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" })).Position;

        var wait = await Assert.ThrowsAsync<LatticeException>(async () => await database.WaitForPositionAsync(foreign));
        var sessionWait = await Assert.ThrowsAsync<LatticeException>(async () => await database.WaitForPositionAsync(new LatticeSession(foreign)));
        var read = await Assert.ThrowsAsync<LatticeException>(async () => await database.Set("User").GetAsync(1L, new LatticeSession(foreign)));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.HistoryMismatch, () => database.Set<User>().InsertAsync(new User { Id = 1, Name = "Grace" }, new LatticeSession(foreign)));

        Assert.Equal(LatticeErrorEnum.HistoryMismatch, wait.Error);
        Assert.Equal(LatticeErrorEnum.HistoryMismatch, sessionWait.Error);
        Assert.Equal(LatticeErrorEnum.HistoryMismatch, read.Error);
        Assert.Null(database.Set<User>().Get(1));

        // The default value is no position at all.
        await Assert.ThrowsAsync<ArgumentException>(async () => await database.WaitForPositionAsync(default(LatticePosition)));
    }
}
