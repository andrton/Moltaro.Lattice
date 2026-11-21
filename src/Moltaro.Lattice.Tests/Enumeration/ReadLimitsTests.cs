using Moltaro.Lattice.TestSupport.Enumeration;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Tests.Enumeration;

// The read limits of the options of a database (LatticeDatabaseOptions.ReadMemoryLimit and
// ReadTimeLimit), apart from what each limit counts (ReadMemoryLimitTests, ReadTimeLimitTests; brief of
// Epic 1, sprint S08, "The limits of a read are options of the database only"); that Validate refuses
// a limit that is set and not positive is stated by LatticeDatabaseOptionsTests:
// - A cancellation stays OperationCanceledException, never ReadLimitExceeded: before the first step,
//   between two steps, and during the wait for the position of the session, also once that wait has
//   lasted longer than the time limit; and at a step after a limit failed the pass, where the limit
//   and the token have both been passed when the step looks (brief of S09, part 2, the second finding
//   of the outside review it carries: a cancellation is reported before a limit is). Inside the wait of
//   a step for a slot of the protection of reads and between the rounds of a step the tests of the
//   engine state it (CancelledStepTests). The time limit is one that the step between the first two
//   cases, which has to return its entity, reaches only if the machine holds it up for two seconds, and
//   the wait for the position lasts a second longer than it (ReadTimes).
// - Without a limit an enumeration of any length runs to its end, also under limits as large as their
//   types allow, from which a deadline has to be computed without overflow.
public sealed class ReadLimitsTests
{
    [Fact]
    public async Task Cancellation_of_an_enumeration_under_read_limits_is_OperationCanceledException_and_never_ReadLimitExceeded()
    {
        await using var database = await TestDatabases.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
            ReadMemoryLimit = 1024 * 1024,
            ReadTimeLimit = ReadTimes.Limit,
        });
        var users = database.Set<User>();
        var records = database.Set("User");

        for (var id = 1L; id <= 10; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }

        // Before the first step.
        using var before = new CancellationTokenSource();
        await before.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await users.EnumerateAsync(new LatticeSession(), before.Token).ToListAsync());

        // Between two steps.
        using var between = new CancellationTokenSource();
        await using var steps = records.EnumerateAsync(new LatticeSession(), between.Token).GetAsyncEnumerator();

        Assert.True(await steps.MoveNextAsync());
        await between.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await steps.MoveNextAsync());

        // During the wait for a position ahead of the database, which no write reaches, once the wait
        // has lasted longer than the time limit.
        using var waiting = new CancellationTokenSource();
        var session = new LatticeSession(TestData.Ahead(database.Position)) { SynchronizationTimeout = Bound };
        await using var patient = users.EnumerateAsync(session, waiting.Token).GetAsyncEnumerator();
        var first = patient.MoveNextAsync().AsTask();

        await Task.Delay(ReadTimes.BeyondTheLimit);
        await waiting.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Bound));
    }

    [Fact]
    public async Task Step_of_a_cancelled_enumeration_that_a_read_limit_has_failed_is_OperationCanceledException_and_not_ReadLimitExceeded()
    {
        // 2,000 Int64 keys take more than 4 KiB however they are remembered (ReadMemoryLimitTests).
        await using var database = await TestDatabases.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
            ReadMemoryLimit = 4 * 1024,
        });
        var users = database.Set<User>();

        for (var id = 1L; id <= 2_000; id++)
        {
            await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
        }

        using var cancellation = new CancellationTokenSource();
        await using var steps = users.EnumerateAsync(new LatticeSession(), cancellation.Token).GetAsyncEnumerator();
        LatticeException? failed = null;

        while (failed is null)
        {
            try
            {
                Assert.True(await steps.MoveNextAsync(), "The pass ended before the memory limit failed it.");
            }
            catch (LatticeException exception)
            {
                failed = exception;
            }
        }

        Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, failed.Error);

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await steps.MoveNextAsync());
    }

    [Fact]
    public async Task Enumeration_without_a_read_limit_runs_to_its_end()
    {
        const int Count = 10_000;
        const int LongTags = 1_000;

        // No limit, and the largest limits the types allow.
        await using var unlimited = await TestDatabases.OpenMasterAsync();
        await using var largest = await TestDatabases.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
            ReadMemoryLimit = long.MaxValue,
            ReadTimeLimit = TimeSpan.MaxValue,
        });

        foreach (var database in new[] { unlimited, largest })
        {
            var users = database.Set<User>();
            var tags = database.Set<Tag>();

            for (var id = 1L; id <= Count; id++)
            {
                await users.InsertAsync(new User { Id = id, Name = $"user {id}" });
            }

            // Keys whose text the pass remembers: a thousand of a thousand characters each.
            for (var tag = 0; tag < LongTags; tag++)
            {
                await tags.InsertAsync(new Tag { Name = $"{tag:D4}".PadRight(1_000, 't'), Color = "red" });
            }

            foreach (var form in EnumerationForms.All)
            {
                var returned = await UserPass.Of(database, form).ToListAsync();

                Assert.Equal(Enumerable.Range(1, Count).Select(id => (long)id), returned.Select(item => item.Id).Order());
                Assert.All(returned, item => Assert.Equal($"user {item.Id}", item.Name));
            }

            Assert.Equal(LongTags, tags.Enumerate().Select(tag => tag.Name).Distinct().Count());
            Assert.Equal(LongTags, (await database.Set("Tag").EnumerateAsync(new LatticeSession()).ToListAsync()).Count);
        }
    }
}
