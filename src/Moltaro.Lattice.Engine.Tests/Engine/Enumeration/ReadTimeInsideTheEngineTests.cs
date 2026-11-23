using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Engine.Tests.Engine.Enumeration;

// The time limit of a read, LatticeDatabaseOptions.ReadTimeLimit, counts the time inside the steps of a pass,
// a wait inside the engine included, and not the time between two steps. A pass whose steps spend longer
// inside the engine than the limit fails with LatticeException, ReadLimitExceeded and Limit ReadTime; what it
// returned before stays valid, and every further step fails the same way.
//
// The time a step spends inside the engine is the test's to decide, without the clock of the machine and
// without waiting: the database takes its timestamps from a source the test drives (ScriptedClock,
// EngineTestInputs.ReadTimestamps), whose every reading is later than the one before it by a step the test
// sets. A step that takes a timestamp when it starts and again where it can stop safely has then spent at
// least one such step inside the engine, however fast or slow the machine is.
//
// - A step longer than the limit. A pass in every form of an enumeration takes its first step while the
//   readings are not apart, and spends nothing; then every reading is the limit and one tick later than the
//   one before, so the next step has spent more than the limit at its first look, and the pass fails there.
// - The limit is the sum over the steps of a pass, not the time of one step. Every reading is 50 ms later than
//   the one before, under a limit of 250 ms: a step that reads at its start, at its round and at its end spends
//   100 ms, so none reaches the limit, and three of them together exceed it. The pass has to fail with ReadTime
//   within six steps, and the test checks from the readings that no step alone spent as long as the limit.
public sealed class ReadTimeInsideTheEngineTests
{
    private const int Keys = 4;

    // The entities of the test of the sum: more than the steps it takes.
    private const int SumKeys = 10;

    private const int MostSteps = 6;

    private static readonly TimeSpan SumLimit = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan SumStep = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan Limit = ReadTimes.Limit;

    [Fact]
    public async Task Step_that_spends_inside_the_engine_longer_than_the_time_limit_fails_the_enumeration_with_ReadTime()
    {
        var clock = new ScriptedClock();
        await using var database = await OpenWithStampsAsync(clock, Limit, Keys);

        foreach (var pass in PassesOf(database))
        {
            await using var steps = pass.GetAsyncEnumerator();

            clock.Step = TimeSpan.Zero;
            Assert.True(await steps.MoveNextAsync());

            var first = steps.Current;

            clock.Step = Limit + TimeSpan.FromTicks(1);

            var (returned, failure) = await RestOfAsync(steps);

            // The pass failed at the step that spent more than the limit, or at the next one, and every further
            // step fails the same way; what it returned before is an entity of its one state.
            ExpectReadTime(failure);
            Assert.InRange(returned.Count, 0, 1);
            Assert.InRange(first, 0, Keys - 1);
            ExpectReadTime(await Record.ExceptionAsync(async () => await steps.MoveNextAsync()));
        }
    }

    [Fact]
    public async Task Time_limit_is_the_sum_of_the_steps_of_a_pass_and_fails_it_though_no_step_reaches_it()
    {
        var clock = new ScriptedClock();
        await using var database = await OpenWithStampsAsync(clock, SumLimit, SumKeys);
        using var steps = database.Set<StampSchema.Stamp>().Enumerate().GetEnumerator();

        clock.Step = SumStep;

        Exception? failure = null;
        var taken = 0;

        while (failure is null && taken < MostSteps)
        {
            var from = clock.Readings;

            failure = Record.Exception(() => Assert.True(steps.MoveNext(), "The pass ended before its time limit failed it."));
            taken++;

            Assert.True(clock.SpanFrom(from) < SumLimit, $"Step {taken} spent {clock.SpanFrom(from).TotalMilliseconds} ms inside the engine by itself, against a limit of {SumLimit.TotalMilliseconds} ms.");
        }

        Assert.True(failure is not null, $"The pass did not fail although {MostSteps} of its steps each spent {SumStep.TotalMilliseconds} ms or more inside the engine, against a limit of {SumLimit.TotalMilliseconds} ms.");
        ExpectReadTime(failure);
    }

    // A master of the engine with the stamps 0 to count - 1, each of the state of its own number, under the
    // time limit given in its options, which takes the timestamps of its reads from the clock.
    private static async Task<EngineDatabase> OpenWithStampsAsync(ScriptedClock clock, TimeSpan readTimeLimit, int count)
    {
        var database = EngineDatabase.Create(
            new LatticeDatabaseOptions
            {
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
                Schema = StampSchema.Schema,
                ReadTimeLimit = readTimeLimit,
            },
            new EngineTestInputs { ReadTimestamps = clock.Source });

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

    // Takes the steps of the pass to its end or to the first step that throws: what it returned, and that
    // exception, null at the end.
    private static async Task<(List<int> Returned, Exception? Failure)> RestOfAsync(IAsyncEnumerator<int> steps)
    {
        var returned = new List<int>();

        try
        {
            while (await steps.MoveNextAsync())
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
