using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.TestSupport.Enumeration;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

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
// a thread of its own; a step that finds no free slot waits for one (FullEpochTableTests). Once the
// steps have waited longer than the limit it lets one held read go, and the steps get through its slot.
// The engine checks the limit at the points of a step where it can stop safely, so the step that waited
// may still return its entity: the pass then fails at its next step, before its end. The limit is that
// of ReadTimes, which the first steps, which do not wait, reach only if the machine holds them up for
// two seconds, and the wait passes it because the test holds the slots a second longer, as long as each
// waiting step has taken its timestamp within that second. Before it counts, the test takes the same
// passes on a database without a limit, so that the first step it times does not include compiling the
// code of a step.
//
// The limit is the sum over the steps of a pass, not the time of one step (review round 1 of S08, part
// 2). With every slot held as above, a pass takes one step after another that waits for a slot, and
// after each wait of 100 ms the test lets one held read go and holds a slot again once the step is
// through: each step spends a little over 100 ms inside the engine, under a limit of 250 ms, so none
// reaches the limit, and three of them together exceed it. The pass has to fail with ReadTime within
// six such steps, 600 ms. The margins: the sum of the waits is at least 100 ms a step by construction,
// since a slot is given only that long after the step began to wait, so no machine is too fast for the
// pass to fail; a slow machine only adds to it. A single step reaches the limit only when it is held up
// for 150 ms beyond its wait, or, for the first step, which waits for nothing, when the machine stalls
// it for 250 ms. The test times every step from outside, which takes at least the time the step spent
// inside the engine: an attempt in which no step took as long as the limit shows that the pass failed
// for the sum, and is the one the test judges. An attempt in which one step did is not a failure of
// the test, since that step may have failed the pass by itself; it says nothing of the sum, and the
// test tries again with a new database. A stalled machine therefore makes the test take longer, and
// fails it only when it stalls a step of every attempt for the whole bound of a minute. What the test
// states is unchanged: in the attempt it judges, the pass fails with ReadTime within six steps, none of
// which reached the limit.
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

    private static readonly TimeSpan Limit = ReadTimes.Limit;

    // How long the steps wait for a slot at the least.
    private static readonly TimeSpan Wait = ReadTimes.BeyondTheLimit;

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
                .Select(enumerator => OwnThread.Run(
                    () =>
                    {
                        started.Signal();

                        return RestOf(enumerator);
                    }))
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
        await using (var warm = await OpenWithStampsAsync(readTimeLimit: null))
        {
            Assert.Equal(Keys, warm.Set<StampSchema.Stamp>().Enumerate().Count());
        }

        var trying = Stopwatch.StartNew();

        for (var attempt = 1; ; attempt++)
        {
            var (failure, longest) = await PassOfWaitedStepsAsync();

            if (longest < SumLimit)
            {
                Assert.True(failure is not null, $"The pass did not fail although {MostWaitedSteps} of its steps each spent {StepWait.TotalMilliseconds} ms inside the engine, against a limit of {SumLimit.TotalMilliseconds} ms.");
                ExpectReadTime(failure);

                return;
            }

            Assert.True(trying.Elapsed < Bound, $"In each of {attempt} attempts a step took as long as the limit by itself, so no attempt could show the sum.");
        }
    }

    // One attempt of the test of the sum: a pass that has taken its first step, then steps that each
    // wait for a slot, until the pass fails or six steps are taken. Returns the failure, null if none,
    // and the longest time a step took, as the test saw it from outside.
    private static async Task<(Exception? Failure, TimeSpan Longest)> PassOfWaitedStepsAsync()
    {
        var slots = new ReadEpochs(new object()).SlotCount;
        var database = await OpenWithStampsAsync(SumLimit, SumKeys);
        var held = new List<HeldRead>();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            using var steps = stamps.Enumerate().GetEnumerator();
            var (failure, longest) = TimedStep(steps);

            if (failure is not null)
            {
                return (failure, longest);
            }

            for (var read = 0; read < slots; read++)
            {
                held.Add(await HoldSlotAsync(database, read));
            }

            // Steps that each wait for a slot, until the pass fails.
            for (var step = 0; step < MostWaitedSteps && failure is null; step++)
            {
                using var waiting = new ManualResetEventSlim();
                var next = OwnThread.Run(
                    () =>
                    {
                        waiting.Set();

                        return TimedStep(steps);
                    });

                Assert.True(waiting.Wait(Bound), "The step did not start.");
                await Task.Delay(StepWait);
                Assert.False(next.IsCompleted, "A step ended while every slot of the epoch table was held.");

                await held[step].ReleaseAsync();

                var (stepFailure, took) = await next.WaitAsync(Bound);

                failure = stepFailure;
                longest = took > longest ? took : longest;

                // The step is through and has left its slot: it is held again for the next step.
                held.Add(await HoldSlotAsync(database, slots + step));
            }

            return (failure, longest);
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

    // One step of the pass, timed from outside: what it failed with, null if it returned an entity, and
    // how long it took.
    private static (Exception? Failure, TimeSpan Took) TimedStep(IEnumerator<StampSchema.Stamp> steps)
    {
        var watch = Stopwatch.StartNew();
        var failure = Record.Exception(() => Assert.True(steps.MoveNext(), "The pass ended before its time limit failed it."));

        return (failure, watch.Elapsed);
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
        var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
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
