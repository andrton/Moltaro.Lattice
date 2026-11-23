using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Waits;

// Write batches whose entities overlap never wait for each other in a cycle, also when they do wait
// (docs/design/write-path.md, "A write batch"): every wait goes to a write admitted earlier. The persistence
// step of the test (HeldPersistence) holds a write, so that the batches behind it wait:
// - batches of the entities (1, 2), (2, 1) and (2, 3), submitted while a write of entity 1 is held, all end,
//   in the order of their calls, and the entities end with the state of the last batch of each;
// - four callers write random batches of two or three of six entities and single writes of one, while the
//   persistence step is held and released over and over: every write ends, each caller's writes take
//   positions in the order of its calls, and each entity ends with the state of the write at the latest
//   position that changed it.
// Account of UniqueSchema; every wait is bounded.
public sealed class OverlappingBatchTests
{
    [Fact]
    public async Task Batches_of_overlapping_entities_that_wait_behind_a_held_write_end_in_the_order_of_their_calls()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
            await accounts.InsertAsync(UniqueSchema.Of(2, "b"));
            storage.Hold();

            var held = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "held")).AsTask();
            await storage.NextAsync();

            var forward = database.CreateWriteBatch();
            forward.Replace(UniqueSchema.Of(1, "a", note: "forward"));
            forward.Replace(UniqueSchema.Of(2, "b", note: "forward"));
            var backward = database.CreateWriteBatch();
            backward.Replace(UniqueSchema.Of(2, "b", note: "backward"));
            backward.Replace(UniqueSchema.Of(1, "a", note: "backward"));
            var third = database.CreateWriteBatch();
            third.Replace(UniqueSchema.Of(2, "b", note: "third"));
            third.Insert(UniqueSchema.Of(3, "c"));

            var batches = new[] { forward.CommitAsync().AsTask(), backward.CommitAsync().AsTask(), third.CommitAsync().AsTask() };

            Assert.All(batches, batch => Assert.False(batch.IsCompleted, "A batch ended while a write of its entities was held."));

            storage.ReleaseAll();

            var heldPosition = PositionSequence.Of((await held.WaitAsync(Bound)).Position);
            var positions = (await Task.WhenAll(batches).WaitAsync(Bound)).Select(result => PositionSequence.Of(result.Position)).ToArray();

            Assert.True(heldPosition < positions[0] && positions[0] < positions[1] && positions[1] < positions[2], $"{heldPosition}: {string.Join(", ", positions)}");
            Assert.Equal("backward", accounts.Get(1L)!.Note);
            Assert.Equal("third", accounts.Get(2L)!.Note);
            Assert.NotNull(accounts.Get(3L));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Random_batches_and_single_writes_of_overlapping_entities_end_in_order_while_the_persistence_step_is_held_and_released(int seed)
    {
        const int Entities = 6;
        const int Callers = 4;
        const int Rounds = 100;

        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        for (var id = 1L; id <= Entities; id++)
        {
            await accounts.InsertAsync(UniqueSchema.Of(id, $"login {id}"));
        }

        using var stop = new CancellationTokenSource();
        var toggler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                storage.Hold();
                await Task.Delay(1);
                storage.ReleaseAll();
                await Task.Yield();
            }
        });

        var log = new List<(ulong Position, long[] Ids, string Note)>();
        var logLock = new Lock();

        async Task CallerAsync(int caller)
        {
            var random = new Random((seed * 100) + caller);
            var last = 0UL;

            for (var round = 0; round < Rounds; round++)
            {
                var note = $"caller {caller}, round {round}";
                long[] ids;
                LatticeWriteResult result;

                if (random.Next(3) == 0)
                {
                    ids = [random.Next(1, Entities + 1)];
                    result = await accounts.ReplaceAsync(UniqueSchema.Of(ids[0], $"login {ids[0]}", note: note)).AsTask().WaitAsync(Bound);
                }
                else
                {
                    ids = [.. Enumerable.Range(1, Entities).OrderBy(_ => random.Next()).Take(random.Next(2, 4)).Select(id => (long)id)];

                    var batch = database.CreateWriteBatch();

                    foreach (var id in ids)
                    {
                        batch.Replace(UniqueSchema.Of(id, $"login {id}", note: note));
                    }

                    result = await batch.CommitAsync().AsTask().WaitAsync(Bound);
                }

                var position = PositionSequence.Of(result.Position);

                Assert.True(position > last, $"Caller {caller} was given {position} after {last}.");
                last = position;

                lock (logLock)
                {
                    log.Add((position, ids, note));
                }
            }
        }

        try
        {
            await Task.WhenAll(Enumerable.Range(0, Callers).Select(caller => Task.Run(() => CallerAsync(caller)))).WaitAsync(Bound);
        }
        finally
        {
            await stop.CancelAsync();
            await toggler;
            storage.ReleaseAll();
        }

        Assert.Equal(log.Count, log.Select(entry => entry.Position).Distinct().Count());

        for (var id = 1L; id <= Entities; id++)
        {
            Assert.Equal(log.Where(entry => entry.Ids.Contains(id)).MaxBy(entry => entry.Position).Note, accounts.Get(id)!.Note);
        }

        Assert.Equal(0, database.Counters.QueuedWrites);
        await database.CloseAsync().AsTask().WaitAsync(Bound);
    }
}
