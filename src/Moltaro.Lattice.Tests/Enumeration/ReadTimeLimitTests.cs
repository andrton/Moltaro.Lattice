using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Tests.Enumeration;

// The time limit of a read, LatticeDatabaseOptions.ReadTimeLimit (brief of Epic 1, sprint S08, "What
// the two limits count", the notes of the coordinator for part 2 and "The limits of a read are
// options of the database only"): it counts the time inside the steps of a pass, added up over the
// pass, and neither the time between two steps nor the wait for the position of a session, which the
// timeout of the session bounds.
// - A caller that is slow between two steps does not use the limit up: with a pause longer than the
//   limit between two steps, every form of a pass completes.
// - The wait for the position of a session does not use it up: a pass whose session depends on a
//   position ahead of the database waits for longer than the limit, and completes once a write
//   reaches the position.
// That a step which spends longer inside the engine than the limit fails the pass with ReadTime is
// stated by the tests of the engine, which can keep a step waiting inside it
// (ReadTimeInsideTheEngineTests in Moltaro.Lattice.Engine.Tests). Time passing is what these tests
// state, so they are the tests of the enumeration that wait for it: the limit is 200 ms, thousands of
// times what the few steps of a pass take inside the engine, and a pause 300 ms. Before it counts, a
// test takes every form of a pass once on a database without limits, so that the first step it times
// does not include compiling the code of a step. On the engine alone: the placeholder validates the
// limit and otherwise does not use it until S12.
public sealed class ReadTimeLimitTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);

    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Caller_that_is_slow_between_two_steps_does_not_use_up_the_time_limit()
    {
        await WarmUpAsync();
        await using var database = await OpenAsync();
        var users = database.Set<User>();

        for (var id = 1L; id <= 3; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }

        foreach (var form in EnumerationForms.All)
        {
            var returned = new List<long>();
            await using var steps = UserPass.Of(database, form).GetAsyncEnumerator();

            while (await steps.MoveNextAsync())
            {
                returned.Add(steps.Current.Id);

                if (returned.Count == 1)
                {
                    await Task.Delay(Pause);
                }
            }

            Assert.Equal([1L, 2L, 3L], returned.Order());
        }
    }

    [Fact]
    public async Task Wait_for_the_position_of_a_session_does_not_use_up_the_time_limit()
    {
        await WarmUpAsync();
        await using var database = await OpenAsync();
        var users = database.Set<User>();
        await users.InsertAsync(new User { Id = 1, Name = "Ada" });

        // Sessions that depend on a position ahead of the database, which they may wait for a minute.
        var ahead = TestData.Ahead(database.Position);
        LatticeSession Patient() => new(ahead) { SynchronizationTimeout = Bound };

        var byClass = users.EnumerateAsync(Patient()).ToListAsync().AsTask();
        var byName = database.Set("User").EnumerateAsync(Patient()).ToListAsync().AsTask();

        await Task.Delay(Pause);

        Assert.False(byClass.IsCompleted, $"The enumeration ended while it waited for its position: {byClass.Exception?.InnerException?.Message}");
        Assert.False(byName.IsCompleted, $"The enumeration ended while it waited for its position: {byName.Exception?.InnerException?.Message}");

        // The next write reaches the position, and both passes complete with what it wrote.
        await users.ReplaceAsync(new User { Id = 1, Name = "Ada Lovelace" });

        Assert.Equal("Ada Lovelace", Assert.Single(await byClass.WaitAsync(Bound)).Name);
        Assert.Equal("Ada Lovelace", Assert.Single(await byName.WaitAsync(Bound))["Name"]);
    }

    // Takes every form of a pass on a database without limits.
    private static async Task WarmUpAsync()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine);
        await database.Set<User>().InsertAsync(new User { Id = 1, Name = "Ada" });

        foreach (var form in EnumerationForms.All)
        {
            Assert.Single(await UserPass.Of(database, form).ToListAsync());
        }
    }

    // A master of the engine whose options limit the time of a read.
    private static ValueTask<ILatticeDatabase> OpenAsync() => TestDatabases.OpenAsync(DatabaseImplementationEnum.Engine, new LatticeDatabaseOptions
    {
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        Schema = TestData.Schema,
        ReadTimeLimit = Limit,
    });
}
