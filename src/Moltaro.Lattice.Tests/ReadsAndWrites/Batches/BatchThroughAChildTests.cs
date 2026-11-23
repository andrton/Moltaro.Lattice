using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// A write batch submitted through a child is executed by its master as one write, all of it or none of it,
// and completes once that child has applied it (the remarks of ILatticeWriteBatch): it inserts, replaces and
// deletes, by class and by name, and a refusal of the master reaches the caller as the master gave it, with
// the number of the failing member and, for a unique index, the name of the index, which is resolved in the
// entity type of that member. The failing member is judged in two phases, as on the master: a member that
// fails a condition of its own is named before any member that violates a unique value, and when none does,
// the lowest-numbered violator, with the unique index declared first in its own entity type. Nothing of a
// refused batch is written on either node. Changed says whether any member changed an entity; a batch in
// which no member has anything to do takes a position of its own, which its child has when it completes.
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

    // Two entity types, each with two unique indexes declared with their identities out of order: the index
    // declared first has the identity 2, and each identity names an index of the other type as well.
    private static readonly LatticeSchema IndexesOutOfOrder = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Member",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Login", "Type": "String" }, { "Id": 3, "Name": "Mail", "Type": "String" } ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 2, "Name": "ByMail", "Fields": [3], "Unique": true },
                { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true }
              ]
            },
            {
              "Id": 2, "Name": "Badge",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Code", "Type": "String" }, { "Id": 3, "Name": "Serial", "Type": "String" } ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 2, "Name": "BySerial", "Fields": [3], "Unique": true },
                { "Id": 1, "Name": "ByCode", "Fields": [2], "Unique": true }
              ]
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

    // Member 1 holds the login "ada" and the mail "ada@", Badge 1 the code "gold" and the serial "s1". The first
    // batch: member 0 takes the login of Member 1, which no member frees, and member 2 replaces a Member that is
    // absent; the condition of member 2 is judged before the unique values. The second: member 0 is valid, and
    // members 1 and 2 each take both values of the published entity of their type; the lowest, a Badge, is
    // named, with BySerial, the index declared first in Badge, whose identity 2 is ByMail in Member.
    [Fact]
    public async Task Refusal_of_a_batch_through_a_child_names_its_failing_member_in_two_phases()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, IndexesOutOfOrder);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();
        await master.Set("Member").InsertAsync(Pair(master, "Member", 1, ("Login", "ada"), ("Mail", "ada@")), session);
        await master.Set("Badge").InsertAsync(Pair(master, "Badge", 1, ("Code", "gold"), ("Serial", "s1")), session);
        await child.WaitForPositionAsync(session);
        var before = master.Position;

        var conditionFirst = child.CreateWriteBatch();
        conditionFirst.Insert(Pair(child, "Member", 2, ("Login", "ada"), ("Mail", "grace@")));
        conditionFirst.Insert(Pair(child, "Badge", 2, ("Code", "silver"), ("Serial", "s2")));
        conditionFirst.Replace(Pair(child, "Member", 404, ("Login", "nobody"), ("Mail", "nobody@")));

        var notFound = await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => conditionFirst.CommitAsync());

        Assert.Equal(2, notFound.OperationIndex);

        var lowestViolator = child.CreateWriteBatch();
        lowestViolator.Insert(Pair(child, "Member", 2, ("Login", "grace"), ("Mail", "grace@")));
        lowestViolator.Insert(Pair(child, "Badge", 2, ("Code", "gold"), ("Serial", "s1")));
        lowestViolator.Insert(Pair(child, "Member", 3, ("Login", "ada"), ("Mail", "ada@")));

        var violation = await TestData.ExpectErrorAsync(LatticeErrorEnum.UniqueConstraintViolation, () => lowestViolator.CommitAsync());

        Assert.Equal(1, violation.OperationIndex);
        Assert.Equal("BySerial", violation.IndexName);
        Assert.Equal(before, master.Position);

        foreach (var node in new ILatticeDatabase[] { master, child })
        {
            Assert.Null(node.Set("Member").Get(2L));
            Assert.Null(node.Set("Badge").Get(2L));
            Assert.Null(node.Set("Member").Get(3L));
        }
    }

    [Fact]
    public async Task Batch_through_a_child_in_which_no_member_changes_anything_takes_a_position_of_its_own_and_moves_the_session()
    {
        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback);
        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        var session = new LatticeSession();
        await master.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" }, session);
        await child.WaitForPositionAsync(session);
        var before = session.MinPosition!.Value;

        var batch = child.CreateWriteBatch();
        batch.Delete<User>(404L);
        batch.Delete("Setting", "absent");
        var result = await batch.CommitAsync(session);

        Assert.False(result.Changed);
        Assert.True(PositionSequence.Of(result.Position) > PositionSequence.Of(before), $"The batch has the position {result.Position}, not later than {before}.");
        Assert.Equal(result.Position, child.Position);
        Assert.Equal(result.Position, session.MinPosition);
        Assert.NotNull(child.Set<User>().Get(1L));
    }

    private static LatticeRecord SettingOf(ILatticeDatabase database, string name, string value) =>
        new(database.Set("Setting").EntityType)
        {
            ["Name"] = name,
            ["Value"] = value,
            ["Priority"] = 1,
        };

    private static LatticeRecord Pair(ILatticeDatabase database, string entityType, long id, (string Field, string Value) first, (string Field, string Value) second) =>
        new(database.Set(entityType).EntityType)
        {
            ["Id"] = id,
            [first.Field] = first.Value,
            [second.Field] = second.Value,
        };

    private static LatticeRecord Row(ILatticeDatabase database, string entityType, long id, string field, string value) =>
        new(database.Set(entityType).EntityType)
        {
            ["Id"] = id,
            [field] = value,
        };
}
