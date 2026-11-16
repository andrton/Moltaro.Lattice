namespace Moltaro.Lattice.Tests.Enumeration;

// What a pass returns when entities change between two of its steps (the remarks of
// ILatticeSet<TEntity>.Enumerate; Q07.3.5 of the decision on queries; brief of Epic 1, sprint S08,
// Acceptance): a change does not end the pass, each primary key appears at most once in it, also when
// its entity is replaced, or deleted and created again, and every entity that existed unchanged
// throughout it is returned. A returned entity is one state that was written. What the documentation
// leaves open is asserted as open:
// - An entity inserted during the pass may be returned or not; when it is, once.
// - An entity replaced before the pass reached it is returned once, in the state before or after the
//   replace: it is present throughout the pass under its key (Q07.3.5: "a continuously present
//   record ... must still be included in a full pass"), and only its state changed. One replaced after
//   it was returned is not returned again.
// - An entity deleted before the pass reached it may be omitted or returned once, with its state
//   before the delete; one deleted after it was returned is not returned again.
// - An entity deleted and created again under its key is returned at most once in the pass: not again
//   when it had been returned, and in either of its states when it had not.
// Each test takes the first step of a pass, makes the change on the thread of the pass, between that
// step and the next, and takes the rest of the steps; the entity the first step returned is the one
// "already returned", any other one "not yet returned". With the inserts, enough entities are inserted
// besides to grow the primary index of the engine several times, which it does by their count: a pass
// then meets a table it has not walked, and must still return each key at most once and every entity
// of the start. Every form of an enumeration, on both implementations: the placeholder takes what a
// pass returns at its first step, which keeps every rule above.
public sealed class ChangeBetweenStepsTests
{
    // The entities of every test, before the change: the users 1 to 6, "user {id}".
    private static readonly long[] Keys = [1, 2, 3, 4, 5, 6];

    // The entities a change inserts besides, 16 times as many as there were.
    private static readonly long[] Inserted = [.. Enumerable.Range(101, 96).Select(id => (long)id)];

    [Theory]
    [MemberData(nameof(EnumerationCases.FormsOnBoth), MemberType = typeof(EnumerationCases))]
    public async Task Insert_between_two_steps_does_not_end_the_pass_and_a_new_entity_is_returned_at_most_once(DatabaseImplementationEnum implementation, string form)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();

        var returned = await PassAsync(database, form, async _ => await InsertAsync(users, Inserted));

        ExpectNoKeyTwice(returned);
        Assert.Equal(Keys, returned.Select(item => item.Id).Where(id => !Inserted.Contains(id)).Order());
        Assert.All(returned, item => Assert.Equal($"user {item.Id}", item.Name));
    }

    [Theory]
    [MemberData(nameof(EnumerationCases.FormsOnBoth), MemberType = typeof(EnumerationCases))]
    public async Task Replace_between_two_steps_returns_each_entity_once_in_one_written_state(DatabaseImplementationEnum implementation, string form)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        long[] others = [];

        // The entity already returned and two that are not yet are replaced.
        var returned = await PassAsync(database, form, async first =>
        {
            others = [.. Keys.Where(key => key != first).Take(2)];

            await users.ReplaceAsync(UserOf(first, "replaced"));

            foreach (var other in others)
            {
                await users.ReplaceAsync(UserOf(other, "replaced"));
            }
        });

        Assert.Equal(Keys, returned.Select(item => item.Id).Order());
        Assert.Equal($"user {returned[0].Id}", returned[0].Name);
        Assert.All(returned.Skip(1), item => Assert.Contains(item.Name, StatesOf(item.Id, others.Contains(item.Id) ? "replaced" : null)));
    }

    [Theory]
    [MemberData(nameof(EnumerationCases.FormsOnBoth), MemberType = typeof(EnumerationCases))]
    public async Task Delete_between_two_steps_does_not_end_the_pass_and_the_deleted_entity_is_returned_at_most_once(DatabaseImplementationEnum implementation, string form)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var other = 0L;

        // The entity already returned and one that is not yet are deleted.
        var returned = await PassAsync(database, form, async first =>
        {
            other = Keys.First(key => key != first);

            Assert.True((await users.DeleteAsync(first)).Changed);
            Assert.True((await users.DeleteAsync(other)).Changed);
        });

        ExpectNoKeyTwice(returned);
        Assert.Equal(Keys.Where(key => key != other), returned.Select(item => item.Id).Where(id => id != other).Order());
        Assert.All(returned, item => Assert.Equal($"user {item.Id}", item.Name));
    }

    [Theory]
    [MemberData(nameof(EnumerationCases.FormsOnBoth), MemberType = typeof(EnumerationCases))]
    public async Task Entity_deleted_and_inserted_again_between_two_steps_is_not_returned_twice(DatabaseImplementationEnum implementation, string form)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var other = 0L;

        // The entity already returned and one that is not yet are deleted and created again, and the
        // index grows.
        var returned = await PassAsync(database, form, async first =>
        {
            other = Keys.First(key => key != first);

            foreach (var key in new[] { first, other })
            {
                Assert.True((await users.DeleteAsync(key)).Changed);
                await users.InsertAsync(UserOf(key, "again"));
            }

            await InsertAsync(users, Inserted);
        });

        // The one already returned is not returned again, the other at most once, in either state.
        ExpectNoKeyTwice(returned);
        Assert.Equal(Keys.Where(key => key != other), returned.Select(item => item.Id).Where(id => id != other && !Inserted.Contains(id)).Order());
        Assert.Equal($"user {returned[0].Id}", returned[0].Name);
        Assert.All(returned.Skip(1), item => Assert.Contains(item.Name, StatesOf(item.Id, item.Id == other ? "again" : null)));
    }

    // Inserts the users of Keys, takes the first step of a pass in the form, makes the change with the
    // key of the entity that step returned, and takes the rest of the steps to the end. What the pass
    // returned, the item of the first step first; a step that fails fails the test.
    private static async Task<List<(long Id, string Name)>> PassAsync(ILatticeDatabase database, string form, Func<long, Task> change)
    {
        await InsertAsync(database.Set<User>(), Keys);

        await using var steps = UserPass.Of(database, form).GetAsyncEnumerator();

        Assert.True(await steps.MoveNextAsync());

        var returned = new List<(long Id, string Name)> { steps.Current };

        await change(steps.Current.Id);

        while (await steps.MoveNextAsync())
        {
            returned.Add(steps.Current);
        }

        return returned;
    }

    private static async Task InsertAsync(ILatticeSet<User> users, long[] keys)
    {
        foreach (var key in keys)
        {
            await users.InsertAsync(UserOf(key, "user"));
        }
    }

    private static User UserOf(long id, string state) => new() { Id = id, Name = $"{state} {id}" };

    // The names a returned entity may have: its first state, and the one the test wrote, if any.
    private static string[] StatesOf(long id, string? written) =>
        written is null ? [$"user {id}"] : [$"user {id}", $"{written} {id}"];

    private static void ExpectNoKeyTwice(List<(long Id, string Name)> returned) =>
        Assert.Equal(returned.Count, returned.Select(item => item.Id).Distinct().Count());
}
