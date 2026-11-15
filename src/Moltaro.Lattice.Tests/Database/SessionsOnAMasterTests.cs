namespace Moltaro.Lattice.Tests.Database;

// On a master every member that takes a session is served, and so is WaitForPositionAsync: a master
// has applied every position it gave out (brief of Epic 1, sprint S06, "Sessions and the write
// batch"). A write with a session moves the session to the position of the write, a read to the
// position of its answer; waiting for a position the master gave out completes at once; a position
// of another database history is HistoryMismatch.
public sealed class SessionsOnAMasterTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Every_write_with_a_session_moves_the_session_to_the_position_of_the_write(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
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

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Every_read_with_a_session_moves_the_session_to_the_position_of_its_answer(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
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

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Waiting_for_a_position_the_master_gave_out_completes_at_once(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
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

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Position_of_another_database_history_is_HistoryMismatch(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        await using var other = await TestDatabases.OpenMasterAsync(implementation);
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
