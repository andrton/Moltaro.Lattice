using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Waits;

// A write batch waits for the latest earlier write not completed that shares an entity with it, and a
// single write whose latest earlier write of its entity is a batch waits for that batch, each holding
// nothing; when that write ends, the waiter is started again from the beginning (the brief of Epic 1,
// sprint S17, point 4; exception X6). So the writes of one entity are executed in the order of their calls,
// and nothing is applied twice or lost:
// - a single write, a batch and a single write of one entity, called while the persistence step holds the
//   first, are published in that order;
// - a batch of two entities each of which has an earlier write not completed is published after both;
// - a batch cancelled while it waits ends cancelled, and nothing of it is written;
// - a fault ends every batch that waits with Unavailable, and nothing of it is written;
// - a batch is one write of the group that takes it, whatever the number of its members, so a group of
//   the commit stage never splits it: its members share the one position of that write.
// The persistence step of the test (HeldPersistence) holds the commit of the first write; Account of
// UniqueSchema; every wait is bounded.
public sealed class SharedEntityWaitTests
{
    [Fact]
    public async Task Single_write_batch_and_single_write_of_one_entity_are_published_in_the_order_of_their_calls()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
            storage.Hold();

            var first = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "first")).AsTask();
            await storage.NextAsync();

            var batch = database.CreateWriteBatch();
            batch.Insert(UniqueSchema.Of(2, "b"));
            batch.Replace(UniqueSchema.Of(1, "a", note: "batch"));
            var batched = batch.CommitAsync().AsTask();
            var last = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "last")).AsTask();

            Assert.False(batched.IsCompleted);
            Assert.False(last.IsCompleted);
            Assert.Equal(3, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            var positions = (await Task.WhenAll(first, batched, last).WaitAsync(Bound)).Select(result => PositionSequence.Of(result.Position)).ToArray();

            Assert.True(positions[0] < positions[1] && positions[1] < positions[2], $"Published at {string.Join(", ", positions)}.");
            Assert.Equal("last", accounts.Get(1L)!.Note);
            Assert.NotNull(accounts.Get(2L));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Batch_of_entities_that_have_earlier_writes_not_completed_is_published_after_all_of_them()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
            await accounts.InsertAsync(UniqueSchema.Of(2, "b"));
            storage.Hold();

            var held = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "one")).AsTask();
            await storage.NextAsync();
            var queued = accounts.ReplaceAsync(UniqueSchema.Of(2, "b", note: "two")).AsTask();

            var batch = database.CreateWriteBatch();
            batch.Replace(UniqueSchema.Of(1, "a", note: "batch"));
            batch.Replace(UniqueSchema.Of(2, "b", note: "batch"));
            var batched = batch.CommitAsync().AsTask();

            Assert.False(batched.IsCompleted);

            storage.ReleaseAll();

            var results = await Task.WhenAll(held, queued, batched).WaitAsync(Bound);

            Assert.True(PositionSequence.Of(results[2].Position) > PositionSequence.Of(results[0].Position));
            Assert.True(PositionSequence.Of(results[2].Position) > PositionSequence.Of(results[1].Position));
            Assert.Equal("batch", accounts.Get(1L)!.Note);
            Assert.Equal("batch", accounts.Get(2L)!.Note);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Batch_cancelled_while_it_waits_ends_cancelled_and_nothing_of_it_is_written()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
            storage.Hold();

            var held = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "held")).AsTask();
            await storage.NextAsync();

            using var cancellation = new CancellationTokenSource();
            var batch = database.CreateWriteBatch();
            batch.Insert(UniqueSchema.Of(2, "b"));
            batch.Replace(UniqueSchema.Of(1, "a", note: "batch"));
            var batched = batch.CommitAsync(cancellation.Token).AsTask();
            var after = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "after")).AsTask();

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batched.WaitAsync(Bound));

            storage.ReleaseAll();

            await held.WaitAsync(Bound);
            await after.WaitAsync(Bound);

            Assert.Equal("after", accounts.Get(1L)!.Note);
            Assert.Null(accounts.Get(2L));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Fault_ends_every_batch_that_waits_with_Unavailable_and_nothing_of_it_is_written()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
            storage.Hold();
            database.ArmFault(EngineFaultPointEnum.BeforePublication);

            var held = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "held")).AsTask();
            await storage.NextAsync();

            var waiting = new List<Task<LatticeWriteResult>>();

            for (var round = 0; round < 3; round++)
            {
                var batch = database.CreateWriteBatch();
                batch.Insert(UniqueSchema.Of(10 + round, $"new {round}"));
                batch.Replace(UniqueSchema.Of(1, "a", note: $"batch {round}"));
                waiting.Add(batch.CommitAsync().AsTask());
            }

            storage.ReleaseAll();

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, (await Assert.ThrowsAsync<LatticeException>(() => held.WaitAsync(Bound))).Error);

            foreach (var batch in waiting)
            {
                Assert.Equal(LatticeErrorEnum.Unavailable, (await Assert.ThrowsAsync<LatticeException>(() => batch.WaitAsync(Bound))).Error);
            }

            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // While the first write is held, a batch of 40 members and 40 single writes of other entities are prepared
    // and queued; once it is released, the commit stage forms groups of at most 32 writes, and the batch is one
    // of them.
    [Fact]
    public async Task Batch_of_more_members_than_a_group_holds_is_one_write_of_one_group()
    {
        const int Members = 40;

        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            storage.Hold();

            var first = accounts.InsertAsync(UniqueSchema.Of(1, "first")).AsTask();
            await storage.NextAsync();

            var batch = database.CreateWriteBatch();

            for (var member = 0; member < Members; member++)
            {
                batch.Insert(UniqueSchema.Of(100 + member, $"member {member}"));
            }

            var batched = batch.CommitAsync().AsTask();
            var singles = Enumerable.Range(0, Members).Select(index => accounts.InsertAsync(UniqueSchema.Of(1000 + index, $"single {index}")).AsTask()).ToList();

            storage.ReleaseAll();

            var result = await batched.WaitAsync(Bound);

            await first.WaitAsync(Bound);
            await Task.WhenAll(singles).WaitAsync(Bound);

            var commits = storage.Commits;

            Assert.Equal(2 + Members, commits.Sum(commit => commit.Count));
            Assert.All(commits, commit => Assert.InRange(commit.Count, 1, CommitGroup.MaxOperations));

            var sequence = PositionSequence.Of(result.Position);

            Assert.Contains(commits, commit => commit.FirstSequence <= sequence && sequence < commit.FirstSequence + (ulong)commit.Count);
            Assert.All(Enumerable.Range(0, Members), member => Assert.NotNull(accounts.Get(100L + member)));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }
}
