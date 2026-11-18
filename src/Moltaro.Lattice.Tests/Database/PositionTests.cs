namespace Moltaro.Lattice.Tests.Database;

// What an application sees of the positions of a master (brief of Epic 1, sprint S06, Acceptance):
// a new database has a valid position before its first write, and the first write returns another
// position, which is then the position of the database. That a write which changes nothing, a delete
// of an absent key, has a position of its own as well (the owner's answer to Q39) is stated by
// ConsistencyTests.Every_result_carries_a_position; such a delete changes the revision of no entity, so a
// condition on a revision read before it still holds.
public sealed class PositionTests
{
    [Fact]
    public async Task New_database_has_a_valid_position_before_its_first_write()
    {
        await using var database = await TestDatabases.OpenMasterAsync();

        Assert.True(database.Position.IsValid);
    }

    [Fact]
    public async Task First_write_returns_another_position_which_is_then_the_position_of_the_database()
    {
        // Through a class in one database, by name in another.
        await using var first = await TestDatabases.OpenMasterAsync();
        var beforeFirst = first.Position;

        var byClass = await first.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });

        Assert.True(byClass.Position.IsValid);
        Assert.NotEqual(beforeFirst, byClass.Position);
        Assert.Equal(byClass.Position, first.Position);

        await using var second = await TestDatabases.OpenMasterAsync();
        var settings = second.Set("Setting");
        var beforeSecond = second.Position;

        var byName = await settings.InsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 });

        Assert.True(byName.Position.IsValid);
        Assert.NotEqual(beforeSecond, byName.Position);
        Assert.Equal(byName.Position, second.Position);
    }

    [Fact]
    public async Task Delete_that_deleted_nothing_changes_the_revision_of_no_entity()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();
        var settings = database.Set("Setting");
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        await settings.InsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 });
        Assert.True(users.TryGet(1, out var user, out var userRevision));
        Assert.True(settings.TryGet("theme", out var setting, out var settingRevision));

        // Through a class and by name.
        Assert.False((await users.DeleteAsync(404)).Changed);
        Assert.False((await settings.DeleteAsync("missing")).Changed);

        Assert.True(users.TryGet(1, out _, out var userRevisionAfter));
        Assert.True(settings.TryGet("theme", out _, out var settingRevisionAfter));
        Assert.Equal(userRevision, userRevisionAfter);
        Assert.Equal(settingRevision, settingRevisionAfter);
        await users.ReplaceAsync(user, userRevision);
        await settings.ReplaceAsync(setting, settingRevision);
    }
}
