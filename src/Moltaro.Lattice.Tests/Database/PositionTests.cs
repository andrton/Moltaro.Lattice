namespace Moltaro.Lattice.Tests.Database;

// What an application sees of the positions of a master (brief of Epic 1, sprint S06, Acceptance):
// a new database has a valid position before its first write, and the first write returns another
// position, which is then the position of the database. By the owner's answer to Q39 a write that
// changes nothing, a delete of an absent key, is published at a position of its own as well and
// changes the revision of no entity. The placeholder keeps the position it had for such a delete and
// is not changed (S12 removes it), so that test runs on the engine only.
public sealed class PositionTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task New_database_has_a_valid_position_before_its_first_write(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);

        Assert.True(database.Position.IsValid);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task First_write_returns_another_position_which_is_then_the_position_of_the_database(DatabaseImplementationEnum implementation)
    {
        // Through a class in one database, by name in another.
        await using var first = await TestDatabases.OpenMasterAsync(implementation);
        var beforeFirst = first.Position;

        var byClass = await first.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });

        Assert.True(byClass.Position.IsValid);
        Assert.NotEqual(beforeFirst, byClass.Position);
        Assert.Equal(byClass.Position, first.Position);

        await using var second = await TestDatabases.OpenMasterAsync(implementation);
        var settings = second.Set("Setting");
        var beforeSecond = second.Position;

        var byName = await settings.InsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 });

        Assert.True(byName.Position.IsValid);
        Assert.NotEqual(beforeSecond, byName.Position);
        Assert.Equal(byName.Position, second.Position);
    }

    [Fact]
    public async Task Delete_of_an_absent_key_has_a_position_of_its_own_and_changes_the_revision_of_no_entity()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine);
        var users = database.Set<User>();
        var settings = database.Set("Setting");
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        await settings.InsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 });
        Assert.True(users.TryGet(1, out var user, out var userRevision));
        Assert.True(settings.TryGet("theme", out var setting, out var settingRevision));
        var before = database.Position;

        // Through a class, by name and with a session: each has a position of its own, not the
        // one before it, and that position is then the position of the database.
        var byClass = await users.DeleteAsync(404);

        Assert.False(byClass.Changed);
        Assert.NotEqual(before, byClass.Position);
        Assert.Equal(byClass.Position, database.Position);

        var byName = await settings.DeleteAsync("missing");

        Assert.False(byName.Changed);
        Assert.NotEqual(byClass.Position, byName.Position);
        Assert.Equal(byName.Position, database.Position);

        var session = new LatticeSession();
        var withSession = await users.DeleteAsync(405, session);

        Assert.False(withSession.Changed);
        Assert.NotEqual(byName.Position, withSession.Position);
        Assert.Equal(withSession.Position, database.Position);
        Assert.Equal(withSession.Position, session.MinPosition);

        // No entity changed: each keeps its revision, and a condition on it still holds.
        Assert.True(users.TryGet(1, out _, out var userRevisionAfter));
        Assert.True(settings.TryGet("theme", out _, out var settingRevisionAfter));
        Assert.Equal(userRevision, userRevisionAfter);
        Assert.Equal(settingRevision, settingRevisionAfter);

        await users.ReplaceAsync(user, userRevision);
        await settings.ReplaceAsync(setting, settingRevision);
    }
}
