using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Tests.Enumeration;

// A step of an enumeration after its database was closed is ObjectDisposedException, as every
// operation after a close is; an enumeration never goes on over a closed database (the remarks of
// ILatticeSet<TEntity>.Enumerate; the planner's answer to Q44, brief of Epic 1, sprint S08, "After a
// close"). The close comes between two steps, from another thread or from the code that walks the
// pass, and the next step fails; what the pass returned before stays as it was. Every step after the
// close is a step after the close, so each fails the same way, and none ends the pass as if it had
// returned everything: the engine of S06 ends its pass after the first failure, so that test is
// skipped until S08, part 2. Every form of an enumeration, on the engine alone: the placeholder takes
// what a pass returns at its first step and returns the rest after a close, which is not changed,
// until S12. Every wait is bounded, so a close that waited for an enumeration standing between two
// steps fails the test instead of hanging it.
public sealed class EnumerationAfterCloseTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Theory]
    [MemberData(nameof(EnumerationCases.FormsOnTheEngine), MemberType = typeof(EnumerationCases))]
    public async Task Step_after_a_close_from_another_thread_is_ObjectDisposedException(string form)
    {
        var database = await OpenAsync();

        try
        {
            await using var steps = UserPass.Of(database, form).GetAsyncEnumerator();
            Assert.True(await steps.MoveNextAsync());
            var first = steps.Current;

            await Task.Run(() => database.CloseAsync().AsTask()).WaitAsync(Bound);

            await Assert.ThrowsAsync<ObjectDisposedException>(async () => await steps.MoveNextAsync());
            Assert.Equal($"user {first.Id}", first.Name);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory(Skip = NotImplemented.EnumerationUnderChange)]
    [MemberData(nameof(EnumerationCases.FormsOnTheEngine), MemberType = typeof(EnumerationCases))]
    public async Task Every_step_after_a_close_is_ObjectDisposedException_and_never_the_end_of_the_pass(string form)
    {
        var database = await OpenAsync();

        try
        {
            await using var steps = UserPass.Of(database, form).GetAsyncEnumerator();
            Assert.True(await steps.MoveNextAsync());

            await Task.Run(() => database.CloseAsync().AsTask()).WaitAsync(Bound);

            for (var step = 0; step < 3; step++)
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(async () => await steps.MoveNextAsync());
            }
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [MemberData(nameof(EnumerationCases.FormsOnTheEngine), MemberType = typeof(EnumerationCases))]
    public async Task Step_after_a_close_from_inside_the_walk_is_ObjectDisposedException(string form)
    {
        var database = await OpenAsync();

        try
        {
            var returned = new List<(long Id, string Name)>();

            await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            {
                await foreach (var item in UserPass.Of(database, form))
                {
                    returned.Add(item);
                    await database.DisposeAsync().AsTask().WaitAsync(Bound);
                }
            });

            var first = Assert.Single(returned);
            Assert.Equal($"user {first.Id}", first.Name);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // A master of the engine with three users.
    private static async Task<ILatticeDatabase> OpenAsync()
    {
        var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine);
        var users = database.Set<User>();

        for (var id = 1L; id <= 3; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }

        return database;
    }
}
