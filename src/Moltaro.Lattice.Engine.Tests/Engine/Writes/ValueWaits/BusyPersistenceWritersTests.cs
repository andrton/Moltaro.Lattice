using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.TestSupport.Concurrency;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// Many writers of one value of a unique index at the same time, while the persistence step completes every
// group on another thread, so that writes are prepared beside unpublished ones, wait for the holder of the
// value and are withdrawn by their tokens while they wait (decision on unique indexes; exception X6):
// - of the inserts and the replaces that move a key onto the value, of which at least one is never
//   cancelled, exactly one succeeds: never two, and never none; every other is refused with the name of
//   the index or ends cancelled;
// - when the owner gives the value up meanwhile, at most one writer takes it, and the value has an owner
//   exactly when one did;
// - readers that search the value meanwhile never find two roots that hold it;
// - every write ends, and the database is left with nothing admitted and a root entry for each entity;
// - chains of writes of several entities that move their keys onto the value and back, some cancelled,
//   keep the order of their calls, and at most one entity is left holding the value.
// The persistence step of the test (YieldingPersistence) completes each group on a thread of the pool.
// Account of UniqueSchema; the rounds are bounded, and every wait is bounded.
public sealed class BusyPersistenceWritersTests
{
    private const int Writers = 12;

    private const int Rounds = 30;

    private const int Readers = 2;

    [Fact]
    public async Task Of_many_writers_of_one_value_withdrawn_at_random_exactly_one_succeeds_and_no_reader_finds_two_owners()
    {
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = new YieldingPersistence() });
        var accounts = database.Set<UniqueSchema.Account>();
        var random = new Random(16);
        var value = "";
        var twice = 0;
        var mostAdmitted = 0;

        try
        {
            // The entities a replace moves onto the value, each with a login of its own.
            for (var id = 1L; id <= Writers; id++)
            {
                await accounts.InsertAsync(UniqueSchema.Of(id, $"own-{id}"));
            }

            await ReadersAndWriter.RunAsync(Readers, reader => () =>
            {
                var login = Volatile.Read(ref value);

                // The first reader also counts the writes admitted at once: with groups of one write, more
                // than one admitted means writes waited for the holder of the value.
                if (reader == 0)
                {
                    mostAdmitted = Math.Max(mostAdmitted, database.Counters.QueuedWrites);
                }

                if (login.Length > 0 && accounts.Index("ByLogin").Find(IndexKey.Equal(login)).Take(2).Count() > 1)
                {
                    Interlocked.Increment(ref twice);
                }
            }, async () =>
            {
                for (var round = 0; round < Rounds; round++)
                {
                    Volatile.Write(ref value, $"taken-{round}");
                    await RoundAsync(database, accounts, random, round);
                }
            });

            Assert.Equal(0, Volatile.Read(ref twice));
            Assert.Equal(0, database.Counters.QueuedWrites);
            Assert.True(mostAdmitted > 2, $"The premise: writes waited for the holder of the value; at most {mostAdmitted} were admitted at once.");

            database.DrainRetired();
            Assert.Equal(accounts.Enumerate().Count(), database.Counters.RootEntries);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // Each of a few entities submits a chain of replaces that move its login onto the value and back to a
    // login of its own, one after the other without waiting, some of them cancelled at random just after
    // their call, all entities at once: the writes of each entity that succeed take positions in the order
    // of its calls, each entity ends with the login of the last of them, and at most one entity holds the
    // value, the one a search finds.
    [Fact]
    public async Task Chains_of_writes_that_move_keys_onto_one_value_and_back_keep_their_order_and_leave_at_most_one_owner()
    {
        const int Entities = 6;
        const int Calls = 40;

        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = new YieldingPersistence() });
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            for (var id = 1L; id <= Entities; id++)
            {
                await accounts.InsertAsync(UniqueSchema.Of(id, $"own-{id}"));
            }

            using var start = new ManualResetEventSlim();

            var chains = Enumerable.Range(1, Entities).Select(entity => Task.Run(async () =>
            {
                var random = new Random(entity);
                var calls = new List<(string Login, Task<LatticeWriteResult> Write)>();
                var cancellations = new List<CancellationTokenSource>();

                start.Wait(Bound);

                for (var call = 0; call < Calls; call++)
                {
                    var login = call % 2 == 0 ? "v" : $"own-{entity}-{call}";
                    var cancellation = new CancellationTokenSource();

                    cancellations.Add(cancellation);
                    calls.Add((login, accounts.ReplaceAsync(UniqueSchema.Of(entity, login), cancellation.Token).AsTask()));

                    if (random.Next(4) == 0)
                    {
                        await cancellation.CancelAsync();
                    }
                }

                var succeeded = new List<(string Login, ulong Sequence)>();

                foreach (var (login, write) in calls)
                {
                    var failure = await Record.ExceptionAsync(() => write.WaitAsync(Bound));

                    if (failure is null)
                    {
                        succeeded.Add((login, PositionSequence.Of(write.Result.Position)));
                    }
                    else if (failure is not OperationCanceledException)
                    {
                        Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, Assert.IsType<LatticeException>(failure).Error);
                    }
                }

                foreach (var cancellation in cancellations)
                {
                    cancellation.Dispose();
                }

                return (Entity: (long)entity, Succeeded: succeeded);
            })).ToList();

            start.Set();

            var owners = new List<long>();

            foreach (var chain in chains)
            {
                var (entity, succeeded) = await chain.WaitAsync(Bound);
                var sequences = succeeded.Select(write => write.Sequence).ToList();

                Assert.Equal([.. sequences.Order()], sequences);

                var last = succeeded.Count == 0 ? $"own-{entity}" : succeeded[^1].Login;

                Assert.Equal(last, accounts.Get(entity)!.Login);

                if (last == "v")
                {
                    owners.Add(entity);
                }
            }

            Assert.True(owners.Count <= 1, $"Entities {string.Join(", ", owners)} hold the value.");
            Assert.Equal(owners, UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // One round: the writers of a new value, some cancelled at random while they wait, exactly one succeeds;
    // then its owner gives it up while other writers want it, and at most one takes it.
    private static async Task RoundAsync(EngineDatabase database, ILatticeSet<UniqueSchema.Account> accounts, Random random, int round)
    {
        var value = $"taken-{round}";
        var keeper = random.Next(Writers);
        var writers = Enumerable.Range(0, Writers).Select(writer => WriterOf(accounts, value, 1000 + (100 * round), writer, cancels: writer != keeper && random.Next(3) == 0)).ToList();
        var succeeded = (await WriteAllAsync(writers)).Where(write => write.Succeeded).Select(write => write.Id).ToList();
        var owner = Assert.Single(succeeded);

        Assert.Equal([owner], UniqueSchema.OwnersOf(accounts, value));
        Assert.Equal(value, accounts.Get(owner)!.Login);

        // The owner moves away while other entities want the value: at most one takes it, and the value has
        // an owner exactly when one did.
        var freeing = new List<Writer> { new(owner, _ => accounts.ReplaceAsync(UniqueSchema.Of(owner, $"left-{round}")).AsTask(), Cancels: false) };

        freeing.AddRange(Enumerable.Range(0, Writers)
            .Select(writer => WriterOf(accounts, value, 1000 + (100 * (round + Rounds)), writer, cancels: random.Next(3) == 0))
            .Where(writer => writer.Id != owner));

        var after = await WriteAllAsync(freeing);
        var taken = after.Skip(1).Where(write => write.Succeeded).Select(write => write.Id).ToList();

        Assert.True(after[0].Succeeded, "The owner did not move away.");
        Assert.True(taken.Count <= 1, $"The value {value} was taken by {taken.Count} writers.");
        Assert.Equal(taken, UniqueSchema.OwnersOf(accounts, value));
        Assert.Equal(0, database.Counters.QueuedWrites);
    }

    // The writer of the value: an insert of a new entity, from the first id given, for an even writer; a
    // replace of entity writer + 1 onto the value for an odd one.
    private static Writer WriterOf(ILatticeSet<UniqueSchema.Account> accounts, string value, long first, int writer, bool cancels)
    {
        var id = writer % 2 == 1 ? writer + 1 : first + writer;

        if (writer % 2 == 1)
        {
            return new Writer(id, token => accounts.ReplaceAsync(UniqueSchema.Of(id, value), token).AsTask(), cancels);
        }

        return new Writer(id, token => accounts.InsertAsync(UniqueSchema.Of(id, value), token).AsTask(), cancels);
    }

    // Runs every write on a thread of the pool at once, cancels the token of each write that cancels just
    // after it began, and returns each with the id of its entity and whether it succeeded; a write that did
    // not succeed was refused with the name of the index or ended cancelled.
    private static async Task<List<(long Id, bool Succeeded)>> WriteAllAsync(List<Writer> writers)
    {
        using var start = new ManualResetEventSlim();
        var cancellations = writers.Select(_ => new CancellationTokenSource()).ToList();
        var results = new List<(long Id, bool Succeeded)>();

        try
        {
            var running = writers.Select((writer, index) => Task.Run(async () =>
            {
                start.Wait(Bound);

                var write = writer.Write(cancellations[index].Token);

                if (writer.Cancels)
                {
                    await Task.Yield();
                    await cancellations[index].CancelAsync();
                }

                return await write;
            })).ToList();

            start.Set();

            for (var index = 0; index < running.Count; index++)
            {
                var failure = await Record.ExceptionAsync(() => running[index].WaitAsync(Bound));

                if (failure is not null and not OperationCanceledException)
                {
                    var refused = Assert.IsType<LatticeException>(failure);

                    Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, refused.Error);
                    Assert.Equal("ByLogin", refused.IndexName);
                }

                results.Add((writers[index].Id, failure is null));
            }
        }
        finally
        {
            foreach (var cancellation in cancellations)
            {
                cancellation.Dispose();
            }
        }

        return results;
    }

    // A write of the value by the entity of the id, and whether its token is cancelled just after it began.
    private sealed record Writer(long Id, Func<CancellationToken, Task<LatticeWriteResult>> Write, bool Cancels);

    // A persistence step that completes every group on a thread of the pool, after its call has returned, as
    // a durable one does: the call only takes the group and assigns its positions as MemoryOnly does.
    private sealed class YieldingPersistence : IPersistence
    {
        private ulong _next = 1;

        public async ValueTask<CommitOutcome> CommitAsync(CommitGroup group, CancellationToken cancellationToken)
        {
            var outcome = new CommitOutcome(_next);

            _next += (ulong)group.Writes.Count;

            await Task.Yield();

            return outcome;
        }
    }
}
