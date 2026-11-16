namespace Moltaro.Lattice.Tests.Enumeration;

// Nothing is held between two steps of an enumeration (the remarks of ILatticeSet<TEntity>.Enumerate;
// brief of Epic 1, sprint S08, "Nothing is held between two steps"):
// - The code of the application that consumes a pass, between two of its steps, may read and write
//   the database, the entity just returned included, and enumerate it in a pass of its own; the pass
//   goes on and returns every entity it had not returned once. The consumer changes an entity only
//   after the pass returned it, so every entity it changes was unchanged until then and is returned:
//   the pass would otherwise have had to return it unchanged.
// - A write from another thread completes while an enumeration stands between two steps: an insert of
//   enough entities to grow the primary index, a replace, a delete and an upsert of the entity just
//   returned. Every wait is bounded, so a write that waited for the enumeration fails the test.
// Every form of an enumeration, on both implementations.
public sealed class ApplicationCodeBetweenStepsTests
{
    private const int Count = 5;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Theory]
    [MemberData(nameof(EnumerationCases.FormsOnBoth), MemberType = typeof(EnumerationCases))]
    public async Task Code_of_the_application_between_two_steps_may_read_and_write_the_database_and_the_pass_goes_on(DatabaseImplementationEnum implementation, string form)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await InsertUsersAsync(users);

        var returned = new List<(long Id, string Name)>();

        // The entities of the start in the order the pass returned them, and what a pass of the
        // consumer's own counted.
        var start = new List<long>();
        var nested = 0;

        await foreach (var item in UserPass.Of(database, form))
        {
            returned.Add(item);

            // The entity just returned is read and replaced.
            Assert.Equal(item.Name, users.Get(item.Id)?.Name);
            await users.ReplaceAsync(new User { Id = item.Id, Name = $"seen {item.Id}" });

            if (item.Id > Count)
            {
                continue;
            }

            start.Add(item.Id);

            switch (start.Count)
            {
                case 1:
                    // Deleted and created again, and a new entity is inserted.
                    Assert.True((await users.DeleteAsync(item.Id)).Changed);
                    await users.InsertAsync(new User { Id = item.Id, Name = $"again {item.Id}" });
                    await users.InsertAsync(new User { Id = 100, Name = "user 100" });
                    break;
                case 2:
                    Assert.True((await users.DeleteAsync(item.Id)).Changed);
                    break;
                case 3:
                    nested = users.Enumerate().Count();
                    break;
            }
        }

        // Every entity of the start once, before the consumer changed it; the new one at most once.
        Assert.Equal(returned.Count, returned.Select(item => item.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, Count).Select(id => (long)id), start.Order());
        Assert.All(returned, item => Assert.Contains(item.Id, new long[] { 1, 2, 3, 4, 5, 100 }));
        Assert.All(returned, item => Assert.Equal($"user {item.Id}", item.Name));

        // What the consumer wrote is there: at the third entity of the start the five of the start,
        // less the one it deleted, and the new one.
        Assert.Equal(Count, nested);
        Assert.Equal($"again {start[0]}", users.Get(start[0])?.Name);
        Assert.Null(users.Get(start[1]));
        Assert.All(start.Skip(2), id => Assert.Equal($"seen {id}", users.Get(id)?.Name));
        Assert.NotNull(users.Get(100L));
    }

    [Theory]
    [MemberData(nameof(EnumerationCases.FormsOnBoth), MemberType = typeof(EnumerationCases))]
    public async Task Write_is_not_blocked_by_an_enumeration_that_stands_between_two_steps(DatabaseImplementationEnum implementation, string form)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        await InsertUsersAsync(users);

        await using var steps = UserPass.Of(database, form).GetAsyncEnumerator();
        Assert.True(await steps.MoveNextAsync());
        var first = steps.Current.Id;

        await Task.Run(async () =>
        {
            for (var id = 1_000L; id < 1_300; id++)
            {
                await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
            }

            await users.ReplaceAsync(new User { Id = first, Name = "replaced" });
            Assert.True((await users.DeleteAsync(first)).Changed);
            await users.UpsertAsync(new User { Id = first, Name = "upserted" });
        }).WaitAsync(Bound);

        Assert.Equal("upserted", users.Get(first)?.Name);
        Assert.Equal(Count + 300, users.Enumerate().Count());
    }

    private static async Task InsertUsersAsync(ILatticeSet<User> users)
    {
        for (var id = 1L; id <= Count; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }
    }
}
