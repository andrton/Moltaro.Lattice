using Moltaro.Lattice.TestSupport.Enumeration;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Enumeration;

// A step of an enumeration after its database was closed is ObjectDisposedException, as every
// operation after a close is; an enumeration never goes on over a closed database (the remarks of
// ILatticeSet<TEntity>.Enumerate; the planner's answer to Q44, brief of Epic 1, sprint S08, "After a
// close"). The close comes between two steps, from another thread or from the code that walks the
// pass, and the next step fails; what the pass returned before stays as it was. Every step after the
// close is a step after the close, so each fails the same way, and none ends the pass as if it had
// returned everything. Every form of an enumeration. Every wait is bounded, so a close that waited
// for an enumeration standing between two steps fails the test instead of hanging it.
//
// A first step that waits for the position of its session is an operation in flight when the close
// comes, which the close does not let finish: it ends in Unavailable (settled by the planner with the
// tests of S12), through the class and by name. The session depends on a position of the history of the
// database that it has not reached (TestData.Ahead).
public sealed class EnumerationAfterCloseTests
{
    [Theory]
    [MemberData(nameof(EnumerationCases.Forms), MemberType = typeof(EnumerationCases))]
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

    [Theory]
    [MemberData(nameof(EnumerationCases.Forms), MemberType = typeof(EnumerationCases))]
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
    [MemberData(nameof(EnumerationCases.Forms), MemberType = typeof(EnumerationCases))]
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

    [Theory]
    [InlineData(EnumerationForms.ByClassWithASession)]
    [InlineData(EnumerationForms.ByNameWithASession)]
    public async Task First_step_that_waits_for_the_position_of_its_session_when_the_database_closes_is_Unavailable(string form)
    {
        var database = await OpenAsync();

        try
        {
            var session = new LatticeSession(TestData.Ahead(database.Position));
            var (steps, first) = FirstStep(database, form, session);

            await using (steps)
            {
                Assert.False(first.IsCompleted, "The first step did not wait for the position of its session.");

                await database.CloseAsync().AsTask().WaitAsync(Bound);

                var exception = await Assert.ThrowsAsync<LatticeException>(() => first.WaitAsync(Bound));
                Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
            }
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The enumerator of the form with the session and its first step, begun.
    private static (IAsyncDisposable Steps, Task<bool> First) FirstStep(ILatticeDatabase database, string form, LatticeSession session)
    {
        if (form == EnumerationForms.ByClassWithASession)
        {
            var byClass = database.Set<User>().EnumerateAsync(session).GetAsyncEnumerator();

            return (byClass, byClass.MoveNextAsync().AsTask());
        }

        var byName = database.Set("User").EnumerateAsync(session).GetAsyncEnumerator();

        return (byName, byName.MoveNextAsync().AsTask());
    }

    // A master with three users.
    private static async Task<ILatticeDatabase> OpenAsync()
    {
        var database = await TestDatabases.OpenMasterAsync();
        var users = database.Set<User>();

        for (var id = 1L; id <= 3; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }

        return database;
    }
}
