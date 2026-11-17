using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// A close waits for the writes admitted before it and for a storage step in progress, and frees the
// arena only then (brief of Epic 1, sprint S09, and exception X6: a close waits for the writes admitted
// and for a storage step in progress; review round 1 of S09, part 2). The storage step of the test
// (HeldCommitStorage) holds a replace of entity 1 while a replace of entity 1 waits behind it in the
// chain of its entity and an insert of entity 2 is prepared and waits in the queue. A close then does
// not complete, and a write made after it is ObjectDisposedException. Once the storage step completes,
// every write admitted before the close is published, the close completes, and every piece of native
// memory the database obtained was returned (CountingNativeAllocator). A close while a write is
// resolved for memory and later writes are held back is stated by RefusalResolutionTests. Every wait
// is bounded.
public sealed class CloseWhileWritesWaitTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Close_while_a_group_is_held_in_the_storage_step_and_writes_wait_completes_once_every_write_is_published()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs
        {
            Arena = new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize },
            Allocator = allocator,
            CommitStorage = storage,
        });
        var pieces = database.Set<PieceSchema.Piece>();
        Task? closing = null;

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10));
            storage.Hold();

            var held = PieceSchema.Of(1, name: 26);
            var heldWrite = pieces.ReplaceAsync(held).AsTask();
            await storage.NextAsync();

            var chained = PieceSchema.Of(1, name: 42);
            var chainedWrite = pieces.ReplaceAsync(chained).AsTask();
            var queued = PieceSchema.Of(2, name: 10, note: 300);
            var queuedWrite = pieces.InsertAsync(queued).AsTask();

            await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(held) + PieceTally.SlotBytesOf(queued));

            closing = database.CloseAsync().AsTask();
            Task<LatticeWriteResult>[] waiting = [heldWrite, chainedWrite, queuedWrite];

            Assert.False(closing.IsCompleted, "The close completed while a group was held in the storage step.");
            Assert.DoesNotContain(waiting, write => write.IsCompleted);
            Assert.True(allocator.Outstanding > 0, "The native memory was returned while writes were in progress.");
            await Assert.ThrowsAsync<ObjectDisposedException>(() => pieces.InsertAsync(PieceSchema.Of(3, name: 10)).AsTask().WaitAsync(Bound));

            storage.ReleaseAll();

            var results = await Task.WhenAll(waiting).WaitAsync(Bound);

            Assert.All(results, result => Assert.True(result.Changed));

            await closing.WaitAsync(Bound);

            Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
            Assert.Equal(0, allocator.Outstanding);
            Assert.Equal(allocator.Obtained, allocator.Returned);
        }
        finally
        {
            storage.ReleaseAll();
            await (closing ?? database.CloseAsync().AsTask()).WaitAsync(Bound);
        }
    }
}
