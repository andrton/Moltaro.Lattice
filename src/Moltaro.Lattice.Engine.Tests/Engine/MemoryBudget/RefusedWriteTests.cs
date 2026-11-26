using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

// A write is refused for memory only when it does not fit beside the stored data: never because of
// memory that other writes hold for a time, and never because of memory that is retired and not
// freed yet. A write the arena refuses gives back what it prepared, frees what is retired and tries
// again; refused again, it is resolved alone: it waits for the writes admitted before it, holds back
// the preparation of later ones and waits for those in flight, waits for the reads then in progress,
// frees what is retired and tries once more. Refused then, it is WriteRejected with Limit Memory, has
// had no effect and never runs later; its token cancels every wait (brief of Epic 1, sprint S09, "When
// a write is refused for memory: the rule after review round 1", which replaces "The refusal" of the
// block after S08 and the bounded retry of the block after the implementation; decision Q14 of the
// decision on write admission, as amended). These are the cases of one refused write beside retired
// memory, reads and earlier writes; those of later writes, of two refused writes and of many rounds
// are stated by RefusalResolutionTests. The database is opened with an arena of two segments of the
// default size, 8 MiB (EngineTestInputs, exception X3), and a piece holds a Binary value of 3 MiB: two
// states of it fit, three do not. A replace retires the state it replaced, which stays retired: a look
// of the writer comes only after a segment of retired bytes or a table's worth of retired things. So:
// - A replace whose new state fits only once the retired one is freed, which no read holds, succeeds.
// - When a read held inside the protection (ArmReadSwitch) holds the retired state, the replace waits
//   for it, which the switch of the engine that runs when a refused write begins to wait tells
//   (EngineDatabase.ArmWaitSwitch): it has not ended when it begins to wait, its token cancels
//   the wait and the write changes nothing, and a replace that waits again succeeds once the read has
//   left.
// - An entity that does not fit even after everything retired is freed is WriteRejected, and nothing
//   of it is left.
// - A write that fits only once an earlier replace, still unpublished because the persistence step of the
//   test holds it (HeldPersistence), has retired its old root and it was freed, waits for that
//   replace and completes; the switch tells that it began to wait. Its token cancels that wait too,
//   and then it has no effect and does not run after the replace.
// - A write that does not fit an otherwise idle database is WriteRejected with Limit Memory, has no
//   effect, leaves the instance Ready and nothing of it among the writes admitted (QueuedWrites), and
//   does not run once memory is freed: no group of the persistence step ever holds it.
// Every wait is bounded.
public sealed class RefusedWriteTests
{
    private const int Large = 3 * 1024 * 1024;

    private static readonly int Segment = new ArenaOptions().SegmentSize;

    private static readonly ArenaOptions TwoSegments = new() { MemoryLimit = 2L * Segment };

    [Fact]
    public async Task Replace_that_the_arena_holds_only_once_retired_memory_no_read_holds_is_freed_succeeds()
    {
        await using var database = await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs
        {
            Arena = TwoSegments,
            Allocator = new NativeAllocator(),
            DisableBackgroundMaintenance = true,
        });
        var pieces = database.Set<PieceSchema.Piece>();

        await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
        await pieces.ReplaceAsync(PieceSchema.Of(1, name: 11, data: Large));

        // The premise: the first state is still retired, so the next one fits only once it is freed.
        Assert.True(database.Counters.Arena.RetiredBytes > 0, "The replaced state was freed before the write that needs its memory.");

        await pieces.ReplaceAsync(PieceSchema.Of(1, name: 12, data: Large));

        Assert.Equal(12, pieces.Get(1L)?.Name.Length);
        ExpectHeld(database, PieceSchema.Of(1, name: 12, data: Large));
    }

    [Fact]
    public async Task Replace_that_needs_memory_a_held_read_still_holds_waits_for_the_read_and_its_token_cancels_the_wait()
    {
        await using var database = await EngineMaster.OpenAsync(PieceSchema.Schema, TwoSegments, new NativeAllocator());
        var pieces = database.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));

        // The held read holds the first state, which the replace retires.
        using var held = await HeldRead.StartAsync(database, () => pieces.Get(1L)?.Name.Length);

        await Task.Run(() => pieces.ReplaceAsync(PieceSchema.Of(1, name: 11, data: Large)).AsTask()).WaitAsync(Bound);

        // The third state fits only once the first is freed: the replace begins to wait, and its token
        // ends the wait. A cancelled write changes nothing.
        using var cancellation = new CancellationTokenSource();
        var cancelledWaits = RefusedWriteWait.Begins(database);
        var cancelled = Task.Run(() => pieces.ReplaceAsync(PieceSchema.Of(1, name: 12, data: Large), cancellation.Token).AsTask());

        await ExpectWaitingAsync(cancelledWaits, cancelled);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
        Assert.Equal(11, pieces.Get(1L)?.Name.Length);

        // A replace that waits again succeeds once the held read has left, which returns the state it
        // found or the one that replaced it.
        var waits = RefusedWriteWait.Begins(database);
        var waiting = Task.Run(() => pieces.ReplaceAsync(PieceSchema.Of(1, name: 13, data: Large)).AsTask());

        await ExpectWaitingAsync(waits, waiting);
        Assert.Contains(await held.ReleaseAsync(), new long?[] { 10, 11 });
        Assert.True((await waiting.WaitAsync(Bound)).Changed);

        Assert.Equal(13, pieces.Get(1L)?.Name.Length);
        ExpectHeld(database, PieceSchema.Of(1, name: 13, data: Large));
    }

    [Fact]
    public async Task Entity_that_the_arena_cannot_hold_after_everything_retired_is_freed_is_WriteRejected_and_leaves_nothing()
    {
        await using var database = await EngineMaster.OpenAsync(PieceSchema.Schema, TwoSegments, new NativeAllocator());
        var pieces = database.Set<PieceSchema.Piece>();

        await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
        await pieces.ReplaceAsync(PieceSchema.Of(1, name: 11, data: Large));

        // Two states of the size of the first and the live one: more than the arena holds, also once
        // the retired state is freed.
        var refused = await Assert.ThrowsAsync<LatticeException>(() => pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: 2 * Large)).AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Null(pieces.Get(2L));
        ExpectHeld(database, PieceSchema.Of(1, name: 11, data: Large));
    }

    [Fact]
    public async Task Write_that_fits_only_once_an_earlier_unpublished_replace_has_retired_its_old_root_waits_for_it_and_completes()
    {
        var storage = new HeldPersistence(holding: false);
        await using var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
            storage.Hold();

            // The replace is prepared and held by the persistence step: the arena holds its new state and
            // the live one, and the insert fits only once the live one is retired and freed.
            var replace = pieces.ReplaceAsync(PieceSchema.Of(1, name: 11, data: Large)).AsTask();
            await storage.NextAsync();
            var waits = RefusedWriteWait.Begins(database);
            var insert = Task.Run(() => pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: Large)).AsTask());

            await ExpectWaitingAsync(waits, insert);
            storage.ReleaseAll();

            Assert.True((await replace.WaitAsync(Bound)).Changed);
            Assert.True((await insert.WaitAsync(Bound)).Changed);
            Assert.Equal(11, pieces.Get(1L)?.Name.Length);
            Assert.Equal(10, pieces.Get(2L)?.Name.Length);
            ExpectHeld(database, PieceSchema.Of(1, name: 11, data: Large), PieceSchema.Of(2, name: 10, data: Large));
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task Write_that_waits_for_an_earlier_write_after_the_arena_refused_it_is_cancelled_by_its_token_and_never_runs()
    {
        var storage = new HeldPersistence(holding: false);
        await using var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
            storage.Hold();

            var replace = pieces.ReplaceAsync(PieceSchema.Of(1, name: 11, data: Large)).AsTask();
            var held = await storage.NextAsync();

            using var cancellation = new CancellationTokenSource();
            var waits = RefusedWriteWait.Begins(database);
            var insert = Task.Run(() => pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: Large), cancellation.Token).AsTask());

            await ExpectWaitingAsync(waits, insert);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => insert.WaitAsync(Bound));
            Assert.False(replace.IsCompleted, "The replace held in the storage step completed before the cancelled insert ended.");
            Assert.Equal(1, database.Counters.QueuedWrites);

            held.Release();

            Assert.True((await replace.WaitAsync(Bound)).Changed);
            Assert.Null(pieces.Get(2L));
            Assert.Equal(2, database.Counters.PublishedSequence);
            ExpectHeld(database, PieceSchema.Of(1, name: 11, data: Large));
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task Write_that_does_not_fit_an_otherwise_idle_database_is_WriteRejected_with_Limit_Memory_and_never_runs_later()
    {
        var storage = new HeldPersistence(holding: false);
        await using var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();
        var large = PieceSchema.Of(2, name: 10, data: 2 * Large);

        await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));

        var refused = await Assert.ThrowsAsync<LatticeException>(() => pieces.InsertAsync(large).AsTask().WaitAsync(Bound));

        // Nothing of it is left among the writes admitted, so nothing of it waits to run.
        Assert.Equal(0, database.Counters.QueuedWrites);
        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
        Assert.Null(pieces.Get(2L));
        Assert.Equal(1, database.Counters.PublishedSequence);
        ExpectHeld(database, PieceSchema.Of(1, name: 10, data: Large));

        // Once memory is freed the arena would hold it, and writes after it complete without it; no
        // group ever held it.
        Assert.True((await pieces.DeleteAsync(1L)).Changed);
        database.DrainRetired();
        await pieces.InsertAsync(PieceSchema.Of(3, name: 10));

        Assert.Null(pieces.Get(2L));
        Assert.Equal(3, database.Counters.PublishedSequence);
        Assert.Equal(0, database.Counters.QueuedWrites);
        Assert.DoesNotContain(storage.Commits.SelectMany(commit => commit.Sizes), size => size == PieceTally.SlotBytesOf(large));
        ExpectHeld(database, PieceSchema.Of(3, name: 10));
    }

    private static async Task<EngineDatabase> OpenAsync(HeldPersistence storage) =>
        await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs { Arena = TwoSegments, Allocator = new NativeAllocator(), Persistence = storage });

    // The write began to wait before it ended: a write that the arena refused instead of waiting, or
    // that succeeded without the memory the held read or the earlier write keeps, ends without
    // beginning to wait.
    private static async Task ExpectWaitingAsync(Task waits, Task write)
    {
        await Task.WhenAny(waits, write).WaitAsync(Bound);

        Assert.True(waits.IsCompleted, "The write ended without waiting for what holds the memory it needs.");
        Assert.False(write.IsCompleted, "The write ended while what holds the memory it needs was in progress.");
    }

    // After a drain with no read in progress, the database holds these pieces alone.
    private static void ExpectHeld(EngineDatabase database, params PieceSchema.Piece[] pieces) =>
        PieceTally.Of(pieces).ExpectHeldBy(database);
}
