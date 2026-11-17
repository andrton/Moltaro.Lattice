using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Engine.Tests.Engine.Enumeration;

// The token of an asynchronous enumeration is honored inside every wait of a step and between the
// rounds of a step, and a cancellation is reported before a limit is (brief of Epic 1, sprint S09,
// part 2, the second of the findings of the outside review it carries; exception X6: the wait for a
// slot has the token wherever the caller gave one). The token given to EnumerateAsync and the one given
// to GetAsyncEnumerator, which WithCancellation and await foreach pass, both cancel. The synchronous
// Enumerate has no token; its step waits for a slot as it does. That the wait for the position of the
// session ends with the token is stated by ReadLimitsTests of Moltaro.Lattice.Tests.
//
// - The wait for a slot. Every slot of the epoch table is held (FullEpochTable), and the next step of a
//   pass that has taken its first one waits for a slot; its token is cancelled after 500 ms. It ends in
//   OperationCanceledException while every slot is still held: by class and by name, with the token
//   given either way, without a time limit, under one it cannot reach in the test, and under one of
//   100 ms that the wait has passed, so that the limit and the token have both been passed when the
//   step looks.
// - Between the rounds of a step. A step walks the index in rounds of 256 words, each inside the
//   protection of reads, and after a growth of the index it walks the grown table from its start. The
//   test makes a long step: 65,536 entities, as many as the main table holds before it grows, every one
//   of them returned by the pass, then one more insert that grows the table and the delete of its key,
//   so that the next step walks every word of the grown table, about 900 rounds, and returns no entity.
//   The test sees that step inside the protection without a switch: a replace retires a root, and a
//   drain cannot free it while a read that may still reach it is inside, which only the step can be
//   (Protection.RetiredMemory). It then cancels the token, and the step ends in OperationCanceledException before its next
//   round instead of walking on to the end of the pass. An attempt in which the step ended before the
//   test saw it inside says nothing and is made again with a new database, within the bound.
public sealed class CancelledStepTests
{
    private const string GivenToEnumerateAsync = "given to EnumerateAsync";

    private const string GivenToTheEnumerator = "given to GetAsyncEnumerator";

    private const string NoLimit = "no time limit";

    private const string LimitNotReached = "a time limit the test cannot reach";

    private const string LimitPassed = "a time limit the wait has passed";

    private const int Keys = 4;

    // As many entities as a main table of 2^14 buckets holds before the next insert grows it
    // (Indexes.IndexTable, "The capacity policy").
    private const int Long = 4 << 14;

    private const ulong Seed = 0x9E3779B97F4A7C15;

    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);

    // How long a step waits for a slot before its token is cancelled.
    private static readonly TimeSpan Wait = 5 * Limit;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    public static TheoryData<string, string, string> Cases => new()
    {
        { EnumerationForms.ByClassWithASession, GivenToEnumerateAsync, LimitNotReached },
        { EnumerationForms.ByNameWithASession, GivenToTheEnumerator, NoLimit },
        { EnumerationForms.ByClassWithASession, GivenToTheEnumerator, LimitPassed },
        { EnumerationForms.ByNameWithASession, GivenToEnumerateAsync, LimitPassed },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Step_cancelled_while_every_slot_of_the_epoch_table_is_held_ends_in_OperationCanceledException_while_the_slots_are_still_held(string form, string token, string limit)
    {
        // The code of a step is compiled before a step is timed.
        await using (var warm = await OpenWithStampsAsync(readTimeLimit: null))
        {
            Assert.Equal(Keys, warm.Set<StampSchema.Stamp>().Enumerate().Count());
            Assert.Equal(Keys, warm.Set("Stamp").Enumerate().Count());
        }

        var database = await OpenWithStampsAsync(TimeLimitOf(limit));

        try
        {
            using var cancellation = new CancellationTokenSource();
            var steps = StepsOf(database, form, token, cancellation.Token);

            try
            {
                Assert.True(await steps.MoveNextAsync(), "The first step returned no entity.");

                using var full = await FullEpochTable.StartAsync(database, 0);

                await full.ExpectCancelledWhileHeldAsync(() => steps.MoveNextAsync().AsTask().GetAwaiter().GetResult(), cancellation, Wait);
            }
            finally
            {
                await steps.DisposeAsync();
            }
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Step_whose_token_is_cancelled_between_two_of_its_rounds_ends_in_OperationCanceledException()
    {
        var trying = Stopwatch.StartNew();

        for (var attempt = 1; ; attempt++)
        {
            var (seen, failure) = await CancelInsideALongStepAsync();

            if (seen)
            {
                Assert.True(failure is OperationCanceledException, $"The step whose token was cancelled between two of its rounds ended with {(failure is null ? "the end of the pass" : failure.GetType().Name)}, not OperationCanceledException.");

                return;
            }

            Assert.True(trying.Elapsed < Bound, $"In each of {attempt} attempts the long step ended before the test saw it inside the protection of reads.");
        }
    }

    // One attempt of the test between the rounds: whether the test saw the long step inside the
    // protection and cancelled it there, and what the step ended with, null for a step that returned.
    private static async Task<(bool Seen, Exception? Failure)> CancelInsideALongStepAsync()
    {
        await using var database = await EngineMaster.OpenAsync(KeySchema.Of("Utf8", "Ordinal", "Int64"), Seed);
        var records = database.Set("Int64");

        for (var key = 0L; key < Long; key++)
        {
            await records.InsertAsync(KeySchema.RecordOf(records, key, "first"));
        }

        using var cancellation = new CancellationTokenSource();
        await using var steps = records.EnumerateAsync(new LatticeSession(), cancellation.Token).GetAsyncEnumerator();

        for (var step = 0; step < Long; step++)
        {
            Assert.True(await steps.MoveNextAsync(), "The pass ended before it returned every entity.");
        }

        // The growth, after the pass has returned every entity, and the delete of the key that grew it.
        var grown = false;

        database.HoldNextGrowth(records.EntityType, () => Volatile.Write(ref grown, true));
        await records.InsertAsync(KeySchema.RecordOf(records, (long)Long, "fresh"));

        Assert.True(Volatile.Read(ref grown), "The insert after the last entity did not grow the primary index.");
        Assert.True((await records.DeleteAsync((long)Long)).Changed);

        database.DrainRetired();
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);

        var longStep = Task.Factory.StartNew(
            () => steps.MoveNextAsync().AsTask().GetAwaiter().GetResult(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // A root retired while the step is inside stays retired through a drain.
        var seen = false;
        var number = 0;

        while (!seen && !longStep.IsCompleted)
        {
            await records.ReplaceAsync(KeySchema.RecordOf(records, 0L, $"replaced {number++}"));
            database.DrainRetired();

            seen = database.Counters.Arena.RetiredBytes > 0;
        }

        if (seen)
        {
            await cancellation.CancelAsync();
        }

        var failure = await Record.ExceptionAsync(() => longStep.WaitAsync(Bound));

        return (seen, failure);
    }

    // A pass in the form given, whose token is given to EnumerateAsync or to GetAsyncEnumerator.
    private static IAsyncEnumerator<object> StepsOf(EngineDatabase database, string form, string token, CancellationToken cancellationToken)
    {
        var toEnumerateAsync = token == GivenToEnumerateAsync ? cancellationToken : CancellationToken.None;
        var toTheEnumerator = token == GivenToTheEnumerator ? cancellationToken : CancellationToken.None;

        if (form == EnumerationForms.ByClassWithASession)
        {
            return database.Set<StampSchema.Stamp>().EnumerateAsync(new LatticeSession(), toEnumerateAsync).GetAsyncEnumerator(toTheEnumerator);
        }

        return database.Set("Stamp").EnumerateAsync(new LatticeSession(), toEnumerateAsync).GetAsyncEnumerator(toTheEnumerator);
    }

    private static TimeSpan? TimeLimitOf(string limit) => limit switch
    {
        NoLimit => null,
        LimitNotReached => TimeSpan.FromHours(1),
        _ => Limit,
    };

    // A master of the engine with the stamps 0 to 3, each of the state of its own number, under the time
    // limit given in its options.
    private static async Task<EngineDatabase> OpenWithStampsAsync(TimeSpan? readTimeLimit)
    {
        var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = StampSchema.Schema,
            ReadTimeLimit = readTimeLimit,
        });

        await database.OpenAsync();

        for (var key = 0; key < Keys; key++)
        {
            await database.Set<StampSchema.Stamp>().InsertAsync(StampSchema.StampOf(key, key));
        }

        return database;
    }
}
