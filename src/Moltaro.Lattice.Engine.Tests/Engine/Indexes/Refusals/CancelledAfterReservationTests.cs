using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;

// A write withdrawn before its publication gives back the nodes its indexes reserved
// (docs/design/ordered-index.md, "The reservation"), and the token of a write is honored until its
// dispatch, also once its indexes are reserved: its call ends in OperationCanceledException, and the write
// has had no effect (decision on mutation commands, "Cancellation at the commit boundary";
// Writes.WriteCoordinator, "Cancellation"). Apart from a fault of the database and the managed heap
// running out, a cancellation is the one failure a write of a master meets after its indexes are reserved
// and before its publication. A replace and a delete of entities of Ranked wait prepared behind a write of
// an entity in no index that the storage step of the test holds (HeldPersistence): the replace moves the
// entry of its entity in ByNumber, keeps it in ByLogin, whose long key it shares with the text entry, and
// rewrites it in ByRegion, its Region now in another case; the delete takes the entries of its entity out
// of all three. Both are prepared and cancelled twice, and the state the test compares with is taken
// after the first time: a withdrawal leaves the pool of an index as an earlier withdrawal of the same
// writes from the same tree left it, however many nodes the pool keeps while nothing waits. After the
// second, the database holds what it held before it (HeldState), every search finds what it found, and
// the instance stays Ready; submitted again without a token, both succeed. An insert withdrawn so is
// stated by IndexPreparationTests.
public sealed class CancelledAfterReservationTests
{
    [Fact]
    public async Task Replace_and_delete_cancelled_after_their_indexes_reserved_nodes_give_every_node_back_and_succeed_when_submitted_again()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, new EngineTestInputs { Persistence = storage });

        try
        {
            var ranked = database.Set<IndexSchema.Ranked>();
            var uncounted = database.Set<IndexSchema.Uncounted>();
            var replaced = new IndexSchema.Ranked { Id = 1, Number = 1, Login = new string('a', 100), Region = "North" };
            var deleted = new IndexSchema.Ranked { Id = 2, Number = 2, Login = "login 2", Region = "South" };
            var moved = new IndexSchema.Ranked { Id = 1, Number = 3, Login = replaced.Login, Region = "NORTH" };

            await ranked.InsertAsync(replaced);
            await ranked.InsertAsync(deleted);
            await uncounted.InsertAsync(new IndexSchema.Uncounted { Id = 1, Number = 1 });

            storage.Hold();
            var first = uncounted.ReplaceAsync(new IndexSchema.Uncounted { Id = 1, Number = 2 }).AsTask();
            await storage.NextAsync();
            await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 1, "the first write is admitted");

            await PrepareAndCancelAsync(database, moved, deleted.Id);
            var held = HeldState.Of(database);
            var heldPrepared = database.Counters.Arena.PreparedBytes;

            var waiting = await PrepareAndCancelAsync(database, moved, deleted.Id);

            // In ByNumber the replace moves one entry while the delete removes another: more than one change
            // of key, the most the pool of an index holds while nothing waits, so the two took nodes from the
            // arena whatever the pool held.
            Assert.True(waiting.Arena.PreparedBytes - heldPrepared >= IndexSchema.NodeBytes, "The replace and the delete reserved no node of their indexes.");

            held.ExpectHeldBy(database);
            Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
            RankedSearch.ExpectFound(database, replaced, [1L]);
            RankedSearch.ExpectFound(database, deleted, [2L]);
            Assert.Empty(ranked.Index("ByNumber").Find(IndexKey.Equal(moved.Number)));

            storage.ReleaseAll();
            await first.WaitAsync(Bound);
            await ranked.ReplaceAsync(moved);
            await ranked.DeleteAsync(deleted.Id);

            RankedSearch.ExpectFound(database, moved, [1L]);
            RankedSearch.ExpectFound(database, deleted, []);
            Assert.Empty(ranked.Index("ByNumber").Find(IndexKey.Equal(replaced.Number)));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // Submits the replace and the delete with one token, waits until both are admitted behind the held
    // write, cancels them and waits until both have ended cancelled; the counters while both waited.
    private static async Task<EngineCounters> PrepareAndCancelAsync(EngineDatabase database, IndexSchema.Ranked moved, long deleted)
    {
        var ranked = database.Set<IndexSchema.Ranked>();

        using var cancellation = new CancellationTokenSource();
        var replace = ranked.ReplaceAsync(moved, cancellation.Token).AsTask();
        var delete = ranked.DeleteAsync(deleted, cancellation.Token).AsTask();
        var waiting = await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 3, "the replace and the delete are admitted");

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replace.WaitAsync(Bound));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delete.WaitAsync(Bound));

        return waiting;
    }
}
