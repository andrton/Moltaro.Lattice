namespace Moltaro.Lattice.Tests.Enumeration;

// An enumeration of the entities of one entity type (ILatticeSet<TEntity>.Enumerate): each primary
// key at most once per pass, and a completed pass includes every entity that existed unchanged
// throughout it, so entities that nobody changes are each returned once (brief of Epic 1, sprint
// S06, Acceptance). A replace, and a change of another entity type, do not end a pass.
//
// What a pass returns when entities are inserted, replaced or deleted between two of its steps is
// stated by ChangeBetweenStepsTests, what the code of the application may do between two steps by
// ApplicationCodeBetweenStepsTests, and a step after a close by EnumerationAfterCloseTests.
public sealed class EnumerationTests
{
    [Fact]
    public async Task Entities_that_nobody_changes_are_each_returned_once()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
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

    [Fact]
    public async Task Replace_or_a_change_of_another_entity_type_during_an_enumeration_does_not_end_it()
    {
        await using var database = await TestDatabases.OpenMasterAsync();
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
