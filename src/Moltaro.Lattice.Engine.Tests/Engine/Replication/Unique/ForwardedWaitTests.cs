using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Unique;

// A write a child forwards waits on its master for the holder of a value of a unique index as a write made
// on the master does, and its reply carries what the master decided (decision on unique indexes, "Children
// forward writes and do not reserve a globally unique value locally"; WriteCoordinator, "Forwarded writes"):
// - behind a prepared delete of the owner it takes the value; behind a prepared insert that claims the value
//   it is refused, and the child names the index;
// - a master that closes while a forwarded write waits completes its close once the write has ended, and
//   the child receives the reply first: the write succeeded.
// The persistence step of the master (HeldPersistence) holds the holders. Account of UniqueSchema; every wait
// is bounded.
public sealed class ForwardedWaitTests
{
    [Fact]
    public async Task Forwarded_write_waits_on_the_master_and_takes_a_freed_value_or_is_refused_for_a_claimed_one_naming_the_index()
    {
        var storage = new HeldPersistence(holding: false);
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage });
        var accounts = master.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));

            await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
            var copies = child.Set<UniqueSchema.Account>();

            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            var commit = await storage.NextAsync();
            var freed = copies.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();

            await CountersWait.UntilAsync(master, counters => counters.QueuedWrites == 2, "the forwarded insert waits for the delete");

            commit.Release();

            Assert.True((await delete.WaitAsync(Bound)).Changed);

            // The forwarded insert is the next group, held: it claims the value while a second one wants it.
            var claim = await storage.NextAsync();
            var claimed = copies.InsertAsync(UniqueSchema.Of(3, "v")).AsTask();

            await CountersWait.UntilAsync(master, counters => counters.QueuedWrites == 2, "the second forwarded insert waits for the claim");
            Assert.False(claimed.IsCompleted, "The second forwarded insert completed while the claim was held.");

            claim.Release();
            storage.ReleaseAll();

            Assert.True((await freed.WaitAsync(Bound)).Changed);
            await UniqueSchema.ExpectTakenAsync("ByLogin", claimed);
            await child.WaitForPositionAsync(master.Position, Bound);
            Assert.Equal([2L], UniqueSchema.OwnersOf(copies, "v"));
            Assert.Equal([2L], UniqueSchema.OwnersOf(accounts, "v"));
            Assert.Equal(0, master.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task Master_that_closes_while_a_forwarded_write_waits_for_a_value_completes_once_it_has_ended_and_the_child_has_its_reply()
    {
        var storage = new HeldPersistence(holding: false);
        var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage });
        var accounts = master.Set<UniqueSchema.Account>();
        Task? closing = null;

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "v"));

            await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
            var copies = child.Set<UniqueSchema.Account>();

            storage.Hold();

            var delete = accounts.DeleteAsync(1L).AsTask();
            await storage.NextAsync();
            var waiter = copies.InsertAsync(UniqueSchema.Of(2, "v")).AsTask();

            await CountersWait.UntilAsync(master, counters => counters.QueuedWrites == 2, "the forwarded insert waits for the delete");

            closing = master.CloseAsync().AsTask();

            Assert.False(closing.IsCompleted, "The master closed while a forwarded write waited for a value.");
            Assert.False(waiter.IsCompleted, "The forwarded insert completed while the delete was held.");

            storage.ReleaseAll();

            Assert.True((await delete.WaitAsync(Bound)).Changed);
            Assert.True((await waiter.WaitAsync(Bound)).Changed);
            await closing.WaitAsync(Bound);
        }
        finally
        {
            storage.ReleaseAll();
            await (closing ?? master.CloseAsync().AsTask()).WaitAsync(Bound);
        }
    }
}
