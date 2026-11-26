using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits.HeldMaster;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// The writes that wait for a value of a unique index against the chains of their entities and against the
// resolution of a write the arena refused (WriteCoordinator; exception X6): the writes of one entity keep the
// order of their calls, of two writes that want one freed value exactly one takes it, and no write is lost
// or left admitted:
// - the later write of a waiter's chain and a waiter of another holder want the same freed value;
// - a waiter and the later write of its holder's chain, which moves the key back, want the value the
//   holder gave up;
// - a waiter admitted before a write the arena refused twice goes on while that write waits for it, and
//   the refused write is resolved after it;
// - a waiter that the arena refuses when it is started again is resolved as any refused write is;
// - in MemoryOnly, the writes a resolution deferred are prepared in one hold and published as one group,
//   so there too a write waits for a prepared delete that frees its value or for a prepared claim, and of
//   the deferred writes of one value exactly one takes it; a held read (HeldRead) keeps the resolution in
//   its wait for the reads.
// The persistence step of the test (HeldPersistence) holds the holders. Account of UniqueSchema; every
// wait is bounded.
public sealed class WaitersAndChainsTests
{
    [Fact]
    public async Task Later_write_of_a_waiters_chain_and_a_waiter_of_another_holder_want_one_freed_value_and_exactly_one_takes_it()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            await accounts.InsertAsync(UniqueSchema.Of(5, "w"));
            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            await storage.NextAsync();

            // The move of entity 5 frees w; the insert of entity 2 waits for the delete, and its replace onto w
            // waits behind it in the chain of entity 2; the insert of entity 3 waits for the move.
            var move = accounts.ReplaceAsync(UniqueSchema.Of(5, "x")).AsTask();
            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();
            var chained = accounts.ReplaceAsync(UniqueSchema.Of(2, "w")).AsTask();
            var other = accounts.InsertAsync(UniqueSchema.Of(3, "w")).AsTask();

            Assert.Equal(5, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            Assert.True((await delete.WaitAsync(Bound)).Changed);
            Assert.True((await move.WaitAsync(Bound)).Changed);

            var taken = (await waiter.WaitAsync(Bound)).Position;
            var outcomes = new[] { await OutcomeAsync(chained), await OutcomeAsync(other) };

            Assert.Single(outcomes, outcome => outcome is not null);

            if (outcomes[0] is { } moved)
            {
                Assert.True(PositionSequence.Of(taken) < PositionSequence.Of(moved), "The replace of entity 2 ran before its insert.");
                Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "w"));
                Assert.Empty(UniqueSchema.OwnersOf(accounts, "v"));
                Assert.Null(accounts.Get(3L));
            }
            else
            {
                Assert.Equal([3L], UniqueSchema.OwnersOf(accounts, "w"));
                Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
            }

            Assert.Equal([5L], UniqueSchema.OwnersOf(accounts, "x"));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Waiter_and_the_later_write_of_its_holders_chain_that_moves_the_key_back_want_the_freed_value_and_exactly_one_takes_it()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var away = accounts.ReplaceAsync(UniqueSchema.Of(1, "y")).AsTask();
            await storage.NextAsync();

            var back = accounts.ReplaceAsync(UniqueSchema.Of(1, "v")).AsTask();
            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();

            Assert.Equal(3, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            var moved = await away.WaitAsync(Bound);
            var outcomes = new[] { await OutcomeAsync(back), await OutcomeAsync(waiter) };

            Assert.True(moved.Changed);
            Assert.Single(outcomes, outcome => outcome is not null);

            if (outcomes[0] is { } returned)
            {
                Assert.True(PositionSequence.Of(moved.Position) < PositionSequence.Of(returned), "The second replace of entity 1 ran before its first.");
                Assert.Equal([1L], UniqueSchema.OwnersOf(accounts, "v"));
                Assert.Null(accounts.Get(2L));
            }
            else
            {
                Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
                Assert.Equal("y", accounts.Get(1L)!.Login);
            }

            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // On an arena of two segments of 4 MiB: entity 1 holds the value and 3 MiB, its delete is held in the
    // persistence step, the waiter wants the value, and an insert of 5 MiB, which does not fit beside entity 1,
    // is refused twice and waits in step 1 of its resolution for the writes admitted before it, the waiter
    // among them. The release publishes the delete: the waiter, admitted before the refused insert, is not
    // deferred; it takes the value and is published, and only then is the insert resolved, and fits.
    [Fact]
    public async Task Waiter_admitted_before_a_write_refused_for_memory_runs_first_and_that_write_is_resolved_after_it()
    {
        var storage = new HeldPersistence(holding: false);
        var arena = new ArenaOptions { MemoryLimit = 2L * new ArenaOptions().SegmentSize + 1024 * 1024 };
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage, Arena = arena });
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v", data: 3 * Mebibyte));
            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            await storage.NextAsync();

            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();
            var refusedWaits = RefusedWriteWait.Begins(database);
            var refused = accounts.InsertAsync(UniqueSchema.Of(4, "r", data: 5 * Mebibyte)).AsTask();

            await Task.WhenAny(refusedWaits, refused).WaitAsync(Bound);
            Assert.True(refusedWaits.IsCompleted, "The insert of 5 MiB did not wait in its resolution.");
            Assert.Equal(3, database.Counters.QueuedWrites);

            storage.ReleaseAll();

            var deleted = await delete.WaitAsync(Bound);
            var taken = await waiter.WaitAsync(Bound);
            var resolved = await refused.WaitAsync(Bound);

            Assert.True(deleted.Changed && taken.Changed && resolved.Changed);
            Assert.True(PositionSequence.Of(deleted.Position) < PositionSequence.Of(taken.Position), "The waiter ran before the delete it waited for.");
            Assert.True(PositionSequence.Of(taken.Position) < PositionSequence.Of(resolved.Position), "The refused insert was resolved before the waiter admitted before it.");
            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // On an arena of three segments of 4 MiB holding entity 9 with 5 MiB: the waiter, an insert of the value
    // with 4 MiB, fits when it is first prepared and waits for the delete of the owner held in the persistence
    // step; an insert of 4 MiB of another value is prepared meanwhile in the room the waiter gave back. Once the
    // delete is published the waiter, started again, does not fit beside both and is resolved: it waits for
    // that insert, is tried once more, and is refused for memory, with nothing of it left. The value stays
    // free, and a write that fits takes it.
    [Fact]
    public async Task Waiter_the_arena_refuses_when_it_is_started_again_is_resolved_and_refused_for_memory_with_nothing_of_it_left()
    {
        var storage = new HeldPersistence(holding: false);
        var arena = new ArenaOptions { MemoryLimit = 3L * new ArenaOptions().SegmentSize + 1024 * 1024 };
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage, Arena = arena });
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(9, "large", data: 5 * Mebibyte));
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));
            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            await storage.NextAsync();

            var waiter = accounts.InsertAsync(UniqueSchema.Of(2, "v", data: 4 * Mebibyte)).AsTask();

            // The waiter gave back its root entry, and the insert beside it takes the room the waiter had.
            Assert.Equal(2, database.Counters.RootEntries);

            var beside = accounts.InsertAsync(UniqueSchema.Of(3, "x", data: 4 * Mebibyte)).AsTask();

            Assert.Equal(3, database.Counters.QueuedWrites);
            Assert.Equal(3, database.Counters.RootEntries);

            storage.ReleaseAll();

            Assert.True((await delete.WaitAsync(Bound)).Changed);
            Assert.True((await beside.WaitAsync(Bound)).Changed);

            var refusal = await Assert.ThrowsAsync<LatticeException>(() => waiter.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.WriteRejected, refusal.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refusal.Limit);
            Assert.Null(accounts.Get(2L));
            Assert.Empty(UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal(0, database.Counters.QueuedWrites);

            database.DrainRetired();
            Assert.Equal(2, database.Counters.RootEntries);

            Assert.True((await accounts.InsertAsync(UniqueSchema.Of(4, "v"))).Changed);
            Assert.Equal([4L], UniqueSchema.OwnersOf(accounts, "v"));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // On an arena of two segments of 4 MiB in MemoryOnly: entity 9 held 3 MiB, which its replace retires while
    // a held read can reach it, so that an insert of 5 MiB is refused twice and waits in its resolution for
    // that read; the writes admitted meanwhile are deferred: the delete of the owner of v, two inserts of v,
    // two inserts of w. Once the read leaves, the insert fits and the deferred writes start in one hold.
    [Fact]
    public async Task In_MemoryOnly_writes_of_one_value_deferred_by_a_resolution_wait_for_one_another_and_exactly_one_takes_it()
    {
        var arena = new ArenaOptions { MemoryLimit = 2L * new ArenaOptions().SegmentSize + 1024 * 1024 };
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Arena = arena });
        var accounts = database.Set<UniqueSchema.Account>();

        await accounts.InsertAsync(UniqueSchema.Of(9, "nine", data: 3 * Mebibyte));
        await accounts.InsertAsync(UniqueSchema.Of(1, "v"));

        using var held = await HeldRead.StartAsync(database, () => accounts.Get(9L)?.Data?.Length);

        await accounts.ReplaceAsync(UniqueSchema.Of(9, "nine"));

        var refusedWaits = RefusedWriteWait.Begins(database);
        var refused = Task.Run(() => accounts.InsertAsync(UniqueSchema.Of(4, "r", data: 5 * Mebibyte)).AsTask());

        await Task.WhenAny(refusedWaits, refused).WaitAsync(Bound);
        Assert.True(refusedWaits.IsCompleted, "The insert of 5 MiB did not wait in its resolution.");

        var delete = accounts.DeleteAsync(1L).AsTask();
        var ofV = new[] { accounts.InsertAsync(UniqueSchema.Of(2, "v")).AsTask(), accounts.InsertAsync(UniqueSchema.Of(3, "v")).AsTask() };
        var ofW = new[] { accounts.InsertAsync(UniqueSchema.Of(5, "w")).AsTask(), accounts.InsertAsync(UniqueSchema.Of(6, "w")).AsTask() };

        Assert.Equal(6, database.Counters.QueuedWrites);

        await held.ReleaseAsync();

        Assert.True((await refused.WaitAsync(Bound)).Changed);
        Assert.True((await delete.WaitAsync(Bound)).Changed);

        var tookV = new List<long>();
        var tookW = new List<long>();

        for (var index = 0; index < 2; index++)
        {
            if (await OutcomeAsync(ofV[index]) is not null)
            {
                tookV.Add(2 + index);
            }

            if (await OutcomeAsync(ofW[index]) is not null)
            {
                tookW.Add(5 + index);
            }
        }

        Assert.Single(tookV);
        Assert.Single(tookW);
        Assert.Equal(tookV, UniqueSchema.OwnersOf(accounts, "v"));
        Assert.Equal(tookW, UniqueSchema.OwnersOf(accounts, "w"));
        Assert.True(database.Counters.LargestCommitGroup > 1, "The premise: the deferred writes were published as one group.");
        Assert.Equal(0, database.Counters.QueuedWrites);
    }

    // The position of a write that succeeded, or null for one refused with the name of ByLogin.
    private static async Task<LatticePosition?> OutcomeAsync(Task<LatticeWriteResult> write)
    {
        var failure = await Record.ExceptionAsync(() => write.WaitAsync(Bound));

        if (failure is null)
        {
            return write.Result.Position;
        }

        var refused = Assert.IsType<LatticeException>(failure);

        Assert.Equal(LatticeErrorEnum.UniqueConstraintViolation, refused.Error);
        Assert.Equal("ByLogin", refused.IndexName);

        return null;
    }
}
