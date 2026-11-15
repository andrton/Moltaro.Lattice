namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// An enumeration of the entities of one entity type (ILatticeSet<TEntity>.Enumerate): each primary
// key at most once per pass, and a completed pass includes every entity that existed unchanged
// throughout it, so entities that nobody changes are each returned once (brief of Epic 1, sprint
// S06, Acceptance).
//
// Until S08 the engine fails an enumeration during which the primary index of its entity type
// changed, with NotSupportedException from the MoveNext that sees the change, instead of returning
// a set it cannot vouch for; what it returned before stays valid. An insert or a delete changes the
// index, a replace does not, and neither does a change of another entity type ("Enumeration until
// S08"). The placeholder enumerates a copy taken when the enumeration starts and does not fail, so
// the tests of the failure run on the engine only.
public sealed class EnumerationTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Entities_that_nobody_changes_are_each_returned_once(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var expected = new Dictionary<long, string>();

        // Inserted, some of them deleted, replaced and upserted again before the enumeration starts.
        for (var id = 1L; id <= 300; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
            expected[id] = $"user {id}";
        }

        for (var id = 3L; id <= 300; id += 3)
        {
            await users.DeleteAsync(id);
            expected.Remove(id);
        }

        for (var id = 5L; id <= 300; id += 5)
        {
            await users.UpsertAsync(new User { Id = id, Name = $"written again {id}" });
            expected[id] = $"written again {id}";
        }

        ExpectEach(expected, users.Enumerate().Select(user => (user.Id, user.Name)));
        ExpectEach(expected, database.Set("User").Enumerate().Select(record => (record.Get<long>("Id"), record.Get<string>("Name")!)));
        ExpectEach(expected, (await users.EnumerateAsync(new LatticeSession()).ToListAsync()).Select(user => (user.Id, user.Name)));
        ExpectEach(expected, (await database.Set("User").EnumerateAsync(new LatticeSession()).ToListAsync()).Select(record => (record.Get<long>("Id"), record.Get<string>("Name")!)));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Replace_or_a_change_of_another_entity_type_during_an_enumeration_does_not_end_it(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var tags = database.Set<Tag>();
        await InsertUsersAsync(users, 5);
        await tags.InsertAsync(new Tag { Name = "old", Color = "red" });

        using var enumerator = users.Enumerate().GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var returned = new List<long> { enumerator.Current.Id };

        // The entity already returned is replaced twice; the index of Tag changes.
        await users.ReplaceAsync(new User { Id = returned[0], Name = "replaced" });
        await users.UpsertAsync(new User { Id = returned[0], Name = "replaced again" });
        await tags.InsertAsync(new Tag { Name = "new", Color = "blue" });
        await tags.DeleteAsync("old");

        while (enumerator.MoveNext())
        {
            returned.Add(enumerator.Current.Id);
        }

        // The four entities that nobody changed are there, and every key once.
        Assert.Equal([1L, 2L, 3L, 4L, 5L], returned.Order());
    }

    [Fact]
    public async Task Enumeration_by_class_during_which_an_entity_is_inserted_fails_and_what_it_returned_stays_valid()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine);
        var users = database.Set<User>();
        await InsertUsersAsync(users, 3);

        using var enumerator = users.Enumerate().GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var first = enumerator.Current;

        await users.InsertAsync(new User { Id = 4, Name = "user 4" });

        Assert.Throws<NotSupportedException>(() => enumerator.MoveNext());
        Assert.Equal($"user {first.Id}", first.Name);

        // Each new enumeration is a pass of its own.
        Assert.Equal([1L, 2L, 3L, 4L], users.Enumerate().Select(user => user.Id).Order());
    }

    [Fact]
    public async Task Enumeration_by_name_during_which_an_entity_is_deleted_fails_and_what_it_returned_stays_valid()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine);
        var users = database.Set("User");
        await InsertUsersAsync(database.Set<User>(), 3);

        using var enumerator = users.Enumerate().GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var first = enumerator.Current;
        var other = first.Get<long>("Id") == 1 ? 2L : 1L;

        await users.DeleteAsync(other);

        Assert.Throws<NotSupportedException>(() => enumerator.MoveNext());
        Assert.Equal($"user {first.Get<long>("Id")}", first["Name"]);
        Assert.Equal(2, users.Enumerate().Count());
    }

    [Fact]
    public async Task Enumeration_with_a_session_during_which_an_entity_is_inserted_fails_and_what_it_returned_stays_valid()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine);
        var users = database.Set<User>();
        await InsertUsersAsync(users, 3);

        await using var enumerator = users.EnumerateAsync(new LatticeSession()).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        var first = enumerator.Current;

        await users.InsertAsync(new User { Id = 4, Name = "user 4" });

        await Assert.ThrowsAsync<NotSupportedException>(async () => await enumerator.MoveNextAsync());
        Assert.Equal($"user {first.Id}", first.Name);
    }

    private static async Task InsertUsersAsync(ILatticeSet<User> users, int count)
    {
        for (var id = 1L; id <= count; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }
    }

    // Every entity of the expected ones once, with its values, and nothing else.
    private static void ExpectEach(Dictionary<long, string> expected, IEnumerable<(long Id, string Name)> returned)
    {
        var list = returned.ToList();

        Assert.Equal(expected.Keys.Order(), list.Select(entity => entity.Id).Order());
        Assert.All(list, entity => Assert.Equal(expected[entity.Id], entity.Name));
    }
}
