namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// A write batch submitted through a child is executed by its master as one write, all of it or none of it,
// and completes once that child has applied it (the remarks of ILatticeWriteBatch; the brief of Epic 1,
// sprint S17, point 7): it inserts, replaces and deletes, by class and by name, and a refusal of the master
// reaches the caller as the master gave it, with the number of the failing member and, for a unique index,
// the name of the index, which is resolved in the entity type of that member. Nothing of a refused batch is
// written on either node. Changed says whether any member changed an entity.
public sealed class BatchThroughAChildTests
{
    private static readonly LatticeSchema TwoUniqueTypes = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Member",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Login", "Type": "String" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true } ]
            },
            {
              "Id": 2, "Name": "Badge",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Code", "Type": "String" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByCode", "Fields": [2], "Unique": true } ]
            }
          ]
        }
        """);

    [Fact]
    public async Task Batch_through_a_child_inserts_replaces_and_deletes_by_class_and_by_name_on_both_nodes()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();
        await master.Set<User>().InsertAsync(new User { Id = 2, Name = "Grace" }, session);
        await master.Set<Tag>().InsertAsync(new Tag { Name = "obsolete", Color = "grey" }, session);
        await master.Set("Setting").InsertAsync(SettingOf(master, "language", "en"), session);
        await master.Set("Setting").InsertAsync(SettingOf(master, "old", "x"), session);
        await child.WaitForPositionAsync(session);

        var batch = child.CreateWriteBatch();
        batch.Insert(new User { Id = 1, Name = "Ada" });
        batch.Replace(new User { Id = 2, Name = "Grace Hopper" });
        batch.Delete<Tag>("obsolete");
        batch.Insert(SettingOf(child, "theme", "dark"));
        batch.Replace(SettingOf(child, "language", "de"));
        batch.Delete("Setting", "old");
        var result = await batch.CommitAsync();

        Assert.True(result.Changed);
        Assert.Equal(result.Position, child.Position);

        foreach (var node in new[] { master, child })
        {
            Assert.Equal("Ada", node.Set<User>().Get(1L)!.Name);
            Assert.Equal("Grace Hopper", node.Set<User>().Get(2L)!.Name);
            Assert.Null(node.Set<Tag>().Get("obsolete"));
            Assert.Equal("dark", node.Set("Setting").Get("theme")!["Value"]);
            Assert.Equal("de", node.Set("Setting").Get("language")!["Value"]);
            Assert.Null(node.Set("Setting").Get("old"));
        }
    }

    [Fact]
    public async Task Refusals_of_a_batch_through_a_child_carry_the_number_of_the_failing_member_and_write_nothing()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();
        await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session);
        await master.Set<User>().InsertAsync(new User { Id = 2, Name = "Grace" }, session);
        await child.WaitForPositionAsync(session);
        var users = child.Set<User>();
        Assert.True(users.TryGet(1L, out var ada, out var adaRevision));
        await master.Set<User>().ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" }, session);
        await child.WaitForPositionAsync(session);
        var before = master.Position;

        var duplicate = child.CreateWriteBatch();
        duplicate.Insert(new Tag { Name = "admin", Color = "red" });
        duplicate.Insert(new User { Id = 2, Name = "Another Grace" });
        Assert.Equal(1, (await TestData.ExpectErrorAsync(LatticeErrorEnum.DuplicateKey, () => duplicate.CommitAsync())).OperationIndex);

        var stale = child.CreateWriteBatch();
        stale.Replace(ada, adaRevision);
        stale.Insert(new Tag { Name = "admin", Color = "red" });
        Assert.Equal(0, (await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => stale.CommitAsync())).OperationIndex);

        var absent = child.CreateWriteBatch();
        absent.Insert(new Tag { Name = "admin", Color = "red" });
        absent.Delete<User>(2L);
        absent.Replace(new User { Id = 404, Name = "Nobody" });
        Assert.Equal(2, (await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => absent.CommitAsync())).OperationIndex);

        Assert.Equal(before, master.Position);

        foreach (var node in new[] { master, child })
        {
            Assert.Null(node.Set<Tag>().Get("admin"));
            Assert.Equal("Grace", node.Set<User>().Get(2L)!.Name);
            Assert.Equal("Ada Lovelace", node.Set<User>().Get(1L)!.Name);
        }
    }

    // Both entity types declare a unique index of the identity 1; the failing member is a Badge, so the
    // refusal names the index of Badge.
    [Fact]
    public async Task Unique_refusal_of_a_batch_through_a_child_names_the_index_of_the_entity_type_of_the_failing_member()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, TwoUniqueTypes);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();
        await master.Set("Badge").InsertAsync(Row(master, "Badge", 1, "Code", "gold"), session);
        await child.WaitForPositionAsync(session);
        var before = master.Position;

        var batch = child.CreateWriteBatch();
        batch.Insert(Row(child, "Member", 1, "Login", "gold"));
        batch.Insert(Row(child, "Badge", 2, "Code", "gold"));

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => batch.CommitAsync());

        Assert.Equal(1, refused.OperationIndex);
        Assert.Equal("ByCode", refused.IndexName);
        Assert.Equal(before, master.Position);
        Assert.Null(master.Set("Member").Get(1L));
        Assert.Null(child.Set("Badge").Get(2L));
    }

    private static LatticeRecord SettingOf(ILatticeDatabase database, string name, string value) =>
        new(database.Set("Setting").EntityType)
        {
            ["Name"] = name,
            ["Value"] = value,
            ["Priority"] = 1,
        };

    private static LatticeRecord Row(ILatticeDatabase database, string entityType, long id, string field, string value) =>
        new(database.Set(entityType).EntityType)
        {
            ["Id"] = id,
            [field] = value,
        };
}
