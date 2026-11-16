using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Engine.Tests.Engine.Enumeration;

// The time limit of a read, LatticeDatabaseOptions.ReadTimeLimit, counts the time inside the steps of
// a pass, a wait inside the engine included (brief of Epic 1, sprint S08, "What the two limits count":
// "waiting for a gate included"; the notes of the coordinator for part 2: a read takes no gate since
// part 1, and can wait for a slot of the epoch table). A pass whose steps spend longer inside the
// engine than the limit fails with LatticeException, ReadLimitExceeded and Limit ReadTime; what it
// returned before stays valid, and every further step fails the same way.
//
// The test keeps a step inside the engine for as long as it likes, without a slow machine: it holds a
// table's worth of reads by key inside the protection of reads (HeldRead), so that every slot of the
// epoch table is held, and then takes the next step of a pass in every form of an enumeration, each on
// a thread of its own; a step that finds no free slot waits for one (FullEpochTableTests). After five
// times the limit it lets one held read go, and the steps get through its slot. The engine checks the
// limit at the points of a step where it can stop safely, so the step that waited may still return its
// entity: the pass then fails at its next step, before its end. The limit is 100 ms, thousands of
// times what the steps take that do not wait. Before it counts, the test takes the same passes on a
// database without a limit, so that the first step it times does not include compiling the code of a
// step.
//
// The limit is the sum over the steps of a pass, not the time of one step (review round 1 of S08, part
// 2). With every slot held as above, a pass takes one step after another that waits for a slot, and
// after each wait of 100 ms the test lets one held read go and holds a slot again once the step is
// through: each step spends a little over 100 ms inside the engine, under a limit of 250 ms, so none
// reaches the limit, and three of them together exceed it. The pass has to fail with ReadTime within
// six such steps, 600 ms. The margins: the sum of the waits is at least 100 ms a step by construction,
// since a slot is given only that long after the step began to wait, so no machine is too fast for the
// pass to fail; a slow machine only adds to it. A single step reaches the limit only when it is held up
// for 150 ms beyond its wait, which a pass that counted each step alone would need in order to fail as
// well, so that pass cannot fail here except on a machine stalled for that long.
//
// Every wait is bounded.
public sealed class ReadTimeInsideTheEngineTests
{
    private const int Keys = 4;

    // The entities of the test of the sum: more than the steps it takes.
    private const int SumKeys = 10;

    private const int MostWaitedSteps = 6;

    private static readonly TimeSpan SumLimit = TimeSpan.FromMilliseconds(250);

    // How long each step of the test of the sum waits for a slot at the least.
    private static readonly TimeSpan StepWait = TimeSpan.FromMilliseconds(100);

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    // How long the steps wait for a slot at the least.
    private static readonly TimeSpan Wait = 5 * Limit;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Step_that_waits_inside_the_engine_longer_than_the_time_limit_fails_the_enumeration_with_ReadTime()
    {
        var slots = new ReadEpochs(new object()).SlotCount;

        await using (var warm = await OpenWithStampsAsync(readTimeLimit: null))
        {
            foreach (var pass in PassesOf(warm))
            {
                Assert.Equal(Keys, await pass.CountAsync());
            }
        }

        var database = await OpenWithStampsAsync(Limit);
        var held = new List<HeldRead>();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();

            // A pass in every form, each of which has taken its first step.
            var steps = PassesOf(database).Select(pass => pass.GetAsyncEnumerator()).ToArray();
            var first = new int[steps.Length];

            for (var pass = 0; pass < steps.Length; pass++)
            {
                Assert.True(await steps[pass].MoveNextAsync());
                first[pass] = steps[pass].Current;
            }

            // Every slot of the epoch table held.
            for (var read = 0; read < slots; read++)
            {
                var key = read % Keys;

                held.Add(await HeldRead.StartAsync(database, () => StampSchema.NumberOf(key, stamps.Get((long)key))));
            }

            // The rest of each pass, on a thread of its own: its next step waits for a slot.
            using var started = new CountdownEvent(steps.Length);
            var rests = steps
                .Select(enumerator => Task.Factory.StartNew(
                    () =>
                    {
                        started.Signal();

                        return RestOf(enumerator);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            Assert.True(started.Wait(Bound), "The steps did not start.");
            await Task.Delay(Wait);
            Assert.All(rests, rest => Assert.False(rest.IsCompleted, "A step ended while every slot of the epoch table was held."));

            Assert.Equal<long?>(0, await held[0].ReleaseAsync());

            var outcomes = await Task.WhenAll(rests).WaitAsync(Bound);

            // Each pass failed at the step that waited or at the next one, and every further step
            // fails the same way; what it returned before is an entity of its one state.
            for (var pass = 0; pass < steps.Length; pass++)
            {
                var (returned, failure) = outcomes[pass];

                ExpectReadTime(failure);
                Assert.InRange(returned.Count, 0, 1);
                Assert.InRange(first[pass], 0, Keys - 1);
                ExpectReadTime(await Record.ExceptionAsync(async () => await steps[pass].MoveNextAsync()));

                await steps[pass].DisposeAsync();
            }
        }
        finally
        {
            foreach (var read in held)
            {
                read.Dispose();
            }

            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Time_limit_is_the_sum_of_the_steps_of_a_pass_and_fails_it_though_no_step_reaches_it()
    {
        var slots = new ReadEpochs(new object()).SlotCount;

        await using (var warm = await OpenWithStampsAsync(readTimeLimit: null))
        {
            Assert.Equal(Keys, warm.Set<StampSchema.Stamp>().Enumerate().Count());
        }

        var database = await OpenWithStampsAsync(SumLimit, SumKeys);
        var held = new List<HeldRead>();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            using var steps = stamps.Enumerate().GetEnumerator();

            Assert.True(steps.MoveNext());

            for (var read = 0; read < slots; read++)
            {
                held.Add(await HoldSlotAsync(database, read));
            }

            // Steps that each wait for a slot, until the pass fails.
            Exception? failure = null;

            for (var step = 0; step < MostWaitedSteps && failure is null; step++)
            {
                using var waiting = new ManualResetEventSlim();
                var next = Task.Factory.StartNew(
                    () =>
                    {
                        waiting.Set();

                        return Record.Exception(() => Assert.True(steps.MoveNext(), "The pass ended before its time limit failed it."));
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                Assert.True(waiting.Wait(Bound), "The step did not start.");
                await Task.Delay(StepWait);
                Assert.False(next.IsCompleted, "A step ended while every slot of the epoch table was held.");

                await held[step].ReleaseAsync();
                failure = await next.WaitAsync(Bound);

                // The step is through and has left its slot: it is held again for the next step.
                held.Add(await HoldSlotAsync(database, slots + step));
            }

            Assert.True(failure is not null, $"The pass did not fail although {MostWaitedSteps} of its steps each spent {StepWait.TotalMilliseconds} ms inside the engine, against a limit of {SumLimit.TotalMilliseconds} ms.");
            ExpectReadTime(failure);
        }
        finally
        {
            foreach (var read in held)
            {
                read.Dispose();
            }

            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // One read by key held inside the protection of reads, of a key the read number gives.
    private static Task<HeldRead> HoldSlotAsync(EngineDatabase database, int read)
    {
        var stamps = database.Set<StampSchema.Stamp>();
        var key = read % Keys;

        return HeldRead.StartAsync(database, () => StampSchema.NumberOf(key, stamps.Get((long)key)));
    }

    // A master of the engine with the stamps 0 to count - 1, each of the state of its own number, under
    // the time limit given in its options.
    private static async Task<EngineDatabase> OpenWithStampsAsync(TimeSpan? readTimeLimit, int count = Keys)
    {
        var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = StampSchema.Schema,
            ReadTimeLimit = readTimeLimit,
        });

        await database.OpenAsync();

        for (var key = 0; key < count; key++)
        {
            await database.Set<StampSchema.Stamp>().InsertAsync(StampSchema.StampOf(key, key));
        }

        return database;
    }

    // A pass in every form, as the key of each entity it returns.
    private static IAsyncEnumerable<int>[] PassesOf(EngineDatabase database) =>
        [.. EnumerationForms.All.Select(form => EnumerationForms.Of(form, database.Set<StampSchema.Stamp>(), database.Set("Stamp"), KeyOf, KeyOf))];

    // The key of an entity, after checking that it is the one state of its key the test wrote.
    private static int KeyOf(StampSchema.Stamp stamp)
    {
        var key = (int)stamp.Id;

        Assert.Equal<long?>(key, StampSchema.NumberOf(key, stamp));

        return key;
    }

    private static int KeyOf(LatticeRecord record)
    {
        var key = (int)record.Get<long>("Id");

        Assert.Equal<long?>(key, StampSchema.NumberOf(key, record));

        return key;
    }

    // Takes the steps of the pass, on the calling thread, to its end or to the first step that throws:
    // what it returned, and that exception, null at the end.
    private static (List<int> Returned, Exception? Failure) RestOf(IAsyncEnumerator<int> steps)
    {
        var returned = new List<int>();

        try
        {
            while (steps.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                returned.Add(steps.Current);
            }
        }
        catch (Exception exception)
        {
            return (returned, exception);
        }

        return (returned, null);
    }

    private static void ExpectReadTime(Exception? failure)
    {
        var exception = Assert.IsType<LatticeException>(failure);

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, exception.Error);
        Assert.Equal(LatticeLimitEnum.ReadTime, exception.Limit);
    }
}
