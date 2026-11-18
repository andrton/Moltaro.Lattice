using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Tests.Database;

// A child loads the whole database of its master before it is ready (brief of Epic 1, sprint S10,
// Acceptance and the planner's blocks of 15 and 17 November 2026): a child opened at the address its
// master reports has the master's schema and every entity, through classes and by name, and is at the
// position of its master in the same history; the child of an empty master is ready at sequence zero;
// every entity on the child has the revision it has on the master, also one that was replaced, or
// deleted and inserted again, before the child connected; an entity of 32 MiB arrives whole. These
// tests state the load: none of them writes on the master while its child loads or after it has opened.
// A load under writes and a child that follows its master are stated by MasterAndChildTests and by the
// tests of the engine (Epic 1, sprint S11). The child connects to its master over loopback TCP.
public sealed class ChildLoadTests
{
    private const int ThirtyTwoMebibytes = 32 * 1024 * 1024;

    [Fact]
    public async Task Child_has_the_schema_and_every_entity_of_its_master_through_classes_and_by_name()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync();
        var masterSettings = master.Set("Setting");
        await master.Users().InsertAsync(new User { Id = 1, Name = "Ada", Email = "ada@example.org", Age = 36, Enabled = true });
        await master.Users().InsertAsync(new User { Id = 2, Name = "Grace" });
        await master.Set<Tag>().InsertAsync(new Tag { Name = "urgent", Color = "red" });
        await masterSettings.InsertAsync(new LatticeRecord(masterSettings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1, ["Payload"] = new byte[] { 1, 2, 3 } });
        await masterSettings.InsertAsync(new LatticeRecord(masterSettings.EntityType) { ["Name"] = "locale", ["Value"] = "en", ["Priority"] = 2 });

        await using var child = await TestDatabases.OpenChildAsync(master);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(master.Schema.Fingerprint, child.Schema.Fingerprint);
        Assert.Equal(master.Schema.EntityTypes.Select(entityType => entityType.Name), child.Schema.EntityTypes.Select(entityType => entityType.Name));

        Assert.Equal([1L, 2L], child.Users().Enumerate().Select(user => user.Id).Order());
        ExpectSameUser(master.Users().Get(1L)!, child.Users().Get(1L));
        ExpectSameUser(master.Users().Get(2L)!, child.Users().Get(2L));
        Assert.Equal("red", child.Set<Tag>().Get("urgent")?.Color);

        var childSettings = child.Set("Setting");
        Assert.Equal(["locale", "theme"], childSettings.Enumerate().Select(setting => setting.Get<string>("Name")).Order(StringComparer.Ordinal));
        ExpectSameSetting(masterSettings.Get("theme")!, childSettings.Get("theme"));
        ExpectSameSetting(masterSettings.Get("locale")!, childSettings.Get("locale"));
    }

    [Fact]
    public async Task Loaded_child_is_at_the_position_of_its_master_in_the_same_history()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync();
        var users = master.Users();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        await users.InsertAsync(new User { Id = 2, Name = "Grace" });
        await users.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });
        await users.DeleteAsync(2L);

        await using var child = await TestDatabases.OpenChildAsync(master);

        Assert.Equal(master.Position, child.Position);
        Assert.Equal(4UL, PositionSequence.Of(child.Position));

        // The position of the master is one of the child's history, and the child has applied it.
        await child.WaitForPositionAsync(master.Position, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Child_of_an_empty_master_is_ready_at_sequence_zero()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync();

        await using var child = await TestDatabases.OpenChildAsync(master);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(master.Position, child.Position);
        Assert.Equal(0UL, PositionSequence.Of(child.Position));
        Assert.Empty(child.Users().Enumerate());
        Assert.Empty(child.Set("Setting").Enumerate());
    }

    [Fact]
    public async Task Every_entity_on_a_loaded_child_has_the_revision_it_has_on_the_master()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync();
        var users = master.Users();

        // Inserted; replaced; deleted and inserted again; inserted and replaced by an upsert.
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        await users.InsertAsync(new User { Id = 2, Name = "Grace" });
        await users.ReplaceAsync(new User { Id = 2, Name = "Grace Hopper" });
        await users.InsertAsync(new User { Id = 3, Name = "Edsger" });
        await users.DeleteAsync(3L);
        await users.InsertAsync(new User { Id = 3, Name = "Edsger Dijkstra" });
        await users.UpsertAsync(new User { Id = 4, Name = "Barbara" });
        await users.UpsertAsync(new User { Id = 4, Name = "Barbara Liskov" });

        await using var child = await TestDatabases.OpenChildAsync(master);

        foreach (var id in new[] { 1L, 2L, 3L, 4L })
        {
            Assert.True(users.TryGet(id, out var onMaster, out var masterRevision));
            Assert.True(child.Users().TryGet(id, out var onChild, out var childRevision), $"The child has no user {id}.");
            Assert.Equal(onMaster.Name, onChild.Name);
            Assert.Equal(masterRevision, childRevision);
        }
    }

    [Fact]
    public async Task Entity_of_32_MiB_is_loaded_by_a_child_and_equals_the_masters()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync();
        var settings = master.Set("Setting");

        // Bytes that do not repeat, so that a part placed in the wrong place or lost would show.
        var payload = new byte[ThirtyTwoMebibytes];
        new Random(42).NextBytes(payload);
        await settings.InsertAsync(new LatticeRecord(settings.EntityType) { ["Name"] = "large", ["Value"] = "v", ["Priority"] = 7, ["Payload"] = payload });

        await using var child = await TestDatabases.OpenChildAsync(master);

        var loaded = child.Set("Setting").Get("large");
        Assert.NotNull(loaded);
        Assert.Equal("v", loaded["Value"]);
        Assert.Equal(7, loaded["Priority"]);
        Assert.True(payload.AsSpan().SequenceEqual(loaded.Get<byte[]>("Payload")), "The Binary value of 32 MiB on the child differs from the master's.");
    }

    private static void ExpectSameUser(User expected, User? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Email, actual.Email);
        Assert.Equal(expected.Age, actual.Age);
        Assert.Equal(expected.Enabled, actual.Enabled);
    }

    private static void ExpectSameSetting(LatticeRecord expected, LatticeRecord? actual)
    {
        Assert.NotNull(actual);

        foreach (var field in expected.EntityType.Fields)
        {
            Assert.Equal(expected[field.Name], actual[field.Name]);
        }
    }
}
