using System.Reflection;

namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// What a caller may conclude from a revision, by the documentation of LatticeRevision (brief of
// Epic 1, sprint S06, "Types and members"; milestone contract, "Write coordinator and revision
// semantics"): a revision identifies one state of one entity. Two revisions of the same entity can
// be compared for equality, and nothing else may be concluded: a revision has no order, cannot be
// turned into a position, and means nothing for another entity. So a revision read from one entity
// never satisfies a condition on another, and an entity recreated under the same key has a new one.
// A revision never matches another database history either (the remarks of LatticeRevision; milestone
// contract: the revision binds history, type and key, and position).
public sealed class RevisionTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Revision_read_from_one_entity_never_satisfies_a_condition_on_another(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var tags = database.Set<Tag>();

        // Two users with the same values but the key, written one after the other, and a tag.
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        await users.InsertAsync(new User { Id = 2, Name = "Ada" });
        await tags.InsertAsync(new Tag { Name = "urgent", Color = "red" });
        Assert.True(users.TryGet(1, out _, out var firstRevision));
        Assert.True(users.TryGet(2, out _, out var secondRevision));
        Assert.True(tags.TryGet("urgent", out _, out var tagRevision));

        // The revision of the later write on the earlier entity and the other way round, and the
        // revision of an entity of another entity type.
        await ExpectConflictsAsync(database, 1, secondRevision);
        await ExpectConflictsAsync(database, 2, firstRevision);
        await ExpectConflictsAsync(database, 1, tagRevision);

        // Nothing was written: each entity keeps its own revision.
        Assert.True(users.TryGet(1, out _, out var firstAfter));
        Assert.True(users.TryGet(2, out _, out var secondAfter));
        Assert.Equal(firstRevision, firstAfter);
        Assert.Equal(secondRevision, secondAfter);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Recreated_entity_has_a_new_revision(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        Assert.True(users.TryGet(1, out _, out var original));

        // Deleted and inserted again with the same values.
        await users.DeleteAsync(1);
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });
        Assert.True(users.TryGet(1, out _, out var reinserted));

        Assert.NotEqual(original, reinserted);
        await ExpectConflictsAsync(database, 1, original);

        // Deleted and created again by an upsert.
        await users.DeleteAsync(1);
        await users.UpsertAsync(new User { Id = 1, Name = "Ada" });
        Assert.True(users.TryGet(1, out var user, out var upserted));

        Assert.NotEqual(original, upserted);
        Assert.NotEqual(reinserted, upserted);
        await ExpectConflictsAsync(database, 1, reinserted);

        // The revision of the entity as it is now satisfies the condition.
        await users.ReplaceAsync(user, upserted);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Two_revisions_of_one_entity_are_equal_while_it_keeps_its_state_and_differ_after_a_write(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        // One state, read through the class, by name and with a session.
        Assert.True(users.TryGet(1, out var user, out var byClass));
        Assert.True(userRecords.TryGet(1L, out _, out var byName));
        var withSession = await users.GetAsync(1, new LatticeSession());

        Assert.Equal(byClass, byName);
        Assert.Equal(byClass, withSession.Revision);

        // A replace is a new state even when it writes the same values.
        await users.ReplaceAsync(user);
        Assert.True(users.TryGet(1, out _, out var afterReplace));

        Assert.NotEqual(byClass, afterReplace);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Revision_read_in_one_database_never_satisfies_a_condition_in_another_with_the_same_writes(DatabaseImplementationEnum implementation)
    {
        await using var first = await TestDatabases.OpenMasterAsync(implementation);
        await using var second = await TestDatabases.OpenMasterAsync(implementation);

        // The same writes in both, so that the entity is at the same place of both histories.
        foreach (var database in new[] { first, second })
        {
            await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });
            await database.Set<User>().ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });
        }

        Assert.True(first.Set<User>().TryGet(1, out _, out var fromFirst));

        await ExpectConflictsAsync(second, 1, fromFirst);

        // Its own revision satisfies the condition.
        Assert.True(second.Set<User>().TryGet(1, out var user, out var fromSecond));
        await second.Set<User>().ReplaceAsync(user, fromSecond);
    }

    [Fact]
    public void Revision_offers_equality_and_nothing_else()
    {
        var type = typeof(LatticeRevision);

        // No order: it implements no comparison and has no comparison operator. No conversion: it
        // has no public property, no public constructor and no conversion operator, so nothing,
        // a position included, can be taken from it.
        Assert.Equal([typeof(IEquatable<LatticeRevision>)], type.GetInterfaces());
        Assert.Empty(type.GetProperties());
        Assert.Empty(type.GetConstructors());
        Assert.Equal(
            ["Equals", "Equals", "GetHashCode", "ToString", "op_Equality", "op_Inequality"],
            type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(method => method.Name)
                .Order(StringComparer.Ordinal));
    }

    // A condition on user key with the given revision is refused, through the class and by name, by
    // a replace and by a delete.
    private static async Task ExpectConflictsAsync(ILatticeDatabase database, long key, LatticeRevision revision)
    {
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        Assert.True(users.TryGet(key, out var user));
        Assert.True(userRecords.TryGet(key, out var record));

        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => users.ReplaceAsync(user, revision));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => users.DeleteAsync(key, revision));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => userRecords.ReplaceAsync(record, revision));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => userRecords.DeleteAsync(key, revision));
    }
}
