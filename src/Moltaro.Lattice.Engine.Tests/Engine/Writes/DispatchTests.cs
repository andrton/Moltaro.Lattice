using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// Cancellation up to the dispatch (brief of Epic 1, sprint S09, Acceptance and the answers of the
// planner on the dispatch and the counters; decision "Cancellation at the commit boundary"). The
// dispatch of a write is the call of ICommitStorage.CommitAsync for the group the commit stage took it
// into. The storage of the test holds a commit until the test releases it (HeldCommitStorage), so the
// commit stage stays busy while the test cancels a write that waits for it. So:
// - A write cancelled before its dispatch is withdrawn: its call ends in OperationCanceledException
//   while the commit stage is still busy, no group ever holds it, and nothing of it is left: neither
//   its prepared capacity (PreparedBytes) nor its place among the writes admitted (QueuedWrites), and
//   after a drain the counters equal the tally of the writes that ran. That holds also when the
//   cancellation comes while the thread that completes the storage step holds the lock of the writes
//   and goes on to form the next group before the withdrawal can run (review round 1 of S09, part 2):
//   the cancellation returned before the dispatch, so the write is never dispatched. The test keeps
//   that thread inside its hold, between the publication and the next group, by the preparation of a
//   write of the published entity that waited behind it, held by the allocator of the test
//   (HeldPreparation), and cancels there.
// - In that same hold (review round 2 of S09, part 2), a write waiting behind a prepared write that the
//   next group withdraws gets its turn, also when every write of the queue was withdrawn and the group
//   is left empty: it completes, and so does a later write of its entity. And a write whose token is
//   cancelled in the hold before its turn comes, while it waits behind a write of its entity, ends
//   cancelled when its turn comes and is not prepared: its condition is not looked at. The cancellation
//   comes inside the hold from the growth of the primary index (EngineDatabase.HoldNextGrowth) that the
//   preparation of an insert of entity 5 makes, the ninth entry of an index whose first table holds
//   eight (InsertEightAsync); the hold cancels the token itself, which only hands the withdrawals to the
//   thread pool.
// - From the dispatch on the call returns the real outcome, and its token is not looked at: a write
//   whose token is cancelled while the storage holds its group is published.
// - A write admitted behind an earlier write of the same entity waits for its turn and holds nothing
//   in the engine meanwhile, while a write of another entity admitted after it is prepared at once.
//   Cancelled while it waits, it has no effect, and the later writes of that entity run in the order
//   of their calls.
// - A replace cancelled while it waits for the commit stage gives back the new root it prepared, and the
//   entity keeps its state.
// - A cancellation that arrives while a write is being prepared takes effect when its preparation ends
//   (brief of S09, "Cancellation before dispatch"): the write is withdrawn before it can be dispatched,
//   what it prepared is freed, and it ends in OperationCanceledException while the commit stage is still
//   busy. When that preparation is refused by the arena, the write does not go on to wait for the
//   writes admitted before it: it ends at once, also while an earlier write is held in the storage step.
//   The write is held while it is being prepared by the allocator of the test (HeldPreparation), at the
//   moment its values need a new segment of the arena. The test asks for the cancellation while the
//   preparation is held and lets the preparation go before it waits for the callbacks of the
//   cancellation, which may need the lock of the writes that the preparation holds.
// Each write is known by its encoded size, which differs from every other's (PieceTally.SlotBytesOf).
// Every wait is bounded; the storage releases every commit when a test ends.
public sealed class DispatchTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Write_cancelled_before_its_dispatch_is_withdrawn_ends_in_OperationCanceledException_and_leaves_nothing()
    {
        var storage = new HeldCommitStorage();
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            var first = PieceSchema.Of(1, name: 10);
            var firstWrite = pieces.InsertAsync(first).AsTask();
            await storage.NextAsync();

            // Prepared, and waiting for the commit stage.
            using var cancellation = new CancellationTokenSource();
            var withdrawn = PieceSchema.Of(2, name: 10, note: 300, data: 70_000);
            var cancelled = pieces.InsertAsync(withdrawn, cancellation.Token).AsTask();
            var counters = await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(first) + PieceTally.SlotBytesOf(withdrawn));

            Assert.Equal(2, counters.QueuedWrites);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            Assert.False(firstWrite.IsCompleted, "The write whose commit is held completed.");

            counters = database.Counters;

            Assert.Equal(PieceTally.SlotBytesOf(first), counters.Arena.PreparedBytes);
            Assert.Equal(1, counters.QueuedWrites);

            storage.ReleaseAll();
            await firstWrite.WaitAsync(Bound);

            Assert.Null(pieces.Get(2L));
            Assert.DoesNotContain(storage.Commits.SelectMany(commit => commit.Sizes), size => size == PieceTally.SlotBytesOf(withdrawn));
            Assert.Equal(0, database.Counters.QueuedWrites);
            Assert.Equal(1, database.Counters.PublishedSequence);
            ExpectHeld(database, first);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_cancelled_while_the_completion_of_the_storage_step_holds_the_lock_is_never_dispatched()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage, allocator);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            // The first segment, which a value of 5 MiB outgrows.
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10));
            storage.Hold();

            // A replace of entity 1 in the storage step, a replace of 5 MiB of it waiting behind it, and
            // the write with the token, prepared and in the queue, whose call has returned its task.
            var first = PieceSchema.Of(1, name: 26);
            var firstWrite = pieces.ReplaceAsync(first).AsTask();
            var held = await storage.NextAsync();
            var behind = PieceSchema.Of(1, name: 42, data: 5 * 1024 * 1024);
            var behindWrite = pieces.ReplaceAsync(behind).AsTask();
            using var cancellation = new CancellationTokenSource();
            var withdrawn = PieceSchema.Of(2, name: 10, note: 300);
            var cancelled = pieces.InsertAsync(withdrawn, cancellation.Token).AsTask();

            await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(first) + PieceTally.SlotBytesOf(withdrawn));

            // The thread that completes the storage step publishes the first replace and prepares the
            // one behind it, in one hold of the lock of the writes; the cancellation comes there.
            using var preparation = HeldPreparation.Arm(allocator);

            held.Release();
            await preparation.HeldAsync();
            cancellation.Cancel();
            preparation.Release();

            var next = await storage.NextAsync();

            Assert.DoesNotContain(next.Sizes, size => size == PieceTally.SlotBytesOf(withdrawn));

            storage.ReleaseAll();

            Assert.True((await firstWrite.WaitAsync(Bound)).Changed);
            Assert.True((await behindWrite.WaitAsync(Bound)).Changed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            Assert.Null(pieces.Get(2L));
            Assert.DoesNotContain(storage.Commits.SelectMany(commit => commit.Sizes), size => size == PieceTally.SlotBytesOf(withdrawn));
            Assert.Equal(0, database.Counters.QueuedWrites);
            ExpectHeld(database, behind);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_behind_a_prepared_write_withdrawn_when_the_next_group_is_formed_runs_also_when_that_group_is_left_empty()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await InsertEightAsync(pieces);
            storage.Hold();

            // A delete of the absent entity 5 in the storage step, and behind it an insert of entity 5
            // with the token; a replace of entity 1 with the token, prepared and in the queue, and a
            // replace of entity 1 without one behind it.
            using var cancellation = new CancellationTokenSource();
            var deleteWrite = pieces.DeleteAsync(5L).AsTask();
            await storage.NextAsync();
            var insertWrite = pieces.InsertAsync(PieceSchema.Of(5, name: 10), cancellation.Token).AsTask();
            var withdrawn = PieceSchema.Of(1, name: 26);
            var withdrawnWrite = pieces.ReplaceAsync(withdrawn, cancellation.Token).AsTask();
            var behindWrite = pieces.ReplaceAsync(PieceSchema.Of(1, name: 42)).AsTask();

            await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(withdrawn));

            // The thread that completes the storage step publishes the delete and prepares the insert,
            // whose growth of the index cancels the token; the next group then finds the cancelled
            // replace alone.
            var grown = ArmCancellationAtTheNextGrowth(database, pieces, cancellation);

            storage.ReleaseAll();

            Assert.False((await deleteWrite.WaitAsync(Bound)).Changed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => insertWrite.WaitAsync(Bound));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawnWrite.WaitAsync(Bound));
            Assert.True(grown.IsCompleted, "The insert of entity 5 did not grow the primary index.");
            Assert.True((await behindWrite.WaitAsync(Bound)).Changed);

            // A later write of entity 1 completes, and no write is left among those admitted.
            var last = PieceSchema.Of(1, name: 58);

            Assert.True((await pieces.ReplaceAsync(last).AsTask().WaitAsync(Bound)).Changed);
            Assert.Equal(0, database.Counters.QueuedWrites);
            Assert.Null(pieces.Get(5L));
            ExpectHeld(database, [last, .. EightPieces().Where(piece => piece.Id != 1)]);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_whose_token_is_cancelled_in_the_hold_that_gives_it_its_turn_ends_cancelled_and_is_not_prepared()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await InsertEightAsync(pieces);
            storage.Hold();

            // A delete of the absent entity 5 in the storage step; behind it an insert of entity 5 with
            // the token, and behind that a replace of entity 5 with the token, which, were it prepared,
            // would find no entity 5 and be NotFound.
            using var cancellation = new CancellationTokenSource();
            var deleteWrite = pieces.DeleteAsync(5L).AsTask();
            await storage.NextAsync();
            var insertWrite = pieces.InsertAsync(PieceSchema.Of(5, name: 10), cancellation.Token).AsTask();
            var replaceWrite = pieces.ReplaceAsync(PieceSchema.Of(5, name: 26), cancellation.Token).AsTask();

            // The preparation of the insert, in the hold that publishes the delete, grows the index and
            // cancels the token; the replace has its turn in the same hold.
            var grown = ArmCancellationAtTheNextGrowth(database, pieces, cancellation);

            storage.ReleaseAll();

            Assert.False((await deleteWrite.WaitAsync(Bound)).Changed);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => insertWrite.WaitAsync(Bound));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replaceWrite.WaitAsync(Bound));
            Assert.True(grown.IsCompleted, "The insert of entity 5 did not grow the primary index.");
            Assert.Equal(0, database.Counters.QueuedWrites);
            Assert.Null(pieces.Get(5L));
            ExpectHeld(database, EightPieces());
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_whose_token_is_cancelled_after_its_dispatch_returns_its_real_outcome()
    {
        var storage = new HeldCommitStorage();
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            using var cancellation = new CancellationTokenSource();
            var piece = PieceSchema.Of(1, name: 10, data: 70_000);
            var write = pieces.InsertAsync(piece, cancellation.Token).AsTask();
            var held = await storage.NextAsync();

            await cancellation.CancelAsync();
            held.Release();

            var result = await write.WaitAsync(Bound);

            Assert.True(result.Changed);
            Assert.Equal(result.Position, database.Position);
            Assert.Equal(10, pieces.Get(1L)?.Name.Length);
            Assert.Equal(0, database.Counters.QueuedWrites);
            ExpectHeld(database, piece);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_cancelled_while_it_waits_behind_an_earlier_write_of_its_entity_has_no_effect_and_the_later_writes_of_that_entity_run_in_order()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 1));
            storage.Hold();

            // Four replaces of entity 1, each with a name of its own length; the first is held by the
            // storage, the others wait for it. A write of entity 2 after them.
            var states = new[] { 10, 26, 42, 58 }.Select(name => PieceSchema.Of(1, name: name)).ToArray();
            using var cancellation = new CancellationTokenSource();
            var replaces = new Task<LatticeWriteResult>[states.Length];

            replaces[0] = pieces.ReplaceAsync(states[0]).AsTask();
            await storage.NextAsync();
            replaces[1] = pieces.ReplaceAsync(states[1], cancellation.Token).AsTask();
            replaces[2] = pieces.ReplaceAsync(states[2]).AsTask();
            replaces[3] = pieces.ReplaceAsync(states[3]).AsTask();

            var other = PieceSchema.Of(2, name: 10, note: 500);
            var otherWrite = pieces.InsertAsync(other).AsTask();

            // The write of entity 2 is prepared while the later replaces of entity 1 hold nothing.
            var counters = await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(states[0]) + PieceTally.SlotBytesOf(other));

            Assert.Equal(5, counters.QueuedWrites);

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replaces[1].WaitAsync(Bound));
            Assert.False(replaces[0].IsCompleted, "The replace held in the storage step completed before the cancelled replace ended.");
            Assert.Equal(4, database.Counters.QueuedWrites);
            Assert.Equal(PieceTally.SlotBytesOf(states[0]) + PieceTally.SlotBytesOf(other), database.Counters.Arena.PreparedBytes);

            storage.ReleaseAll();
            await Task.WhenAll([replaces[0], replaces[2], replaces[3], otherWrite]).WaitAsync(Bound);

            var sequences = new[] { replaces[0], replaces[2], replaces[3] }.Select(replace => PositionSequence.Of(replace.Result.Position)).ToArray();

            Assert.Equal(sequences.Order(), sequences);
            Assert.Equal(58, pieces.Get(1L)?.Name.Length);
            Assert.DoesNotContain(storage.Commits.SelectMany(commit => commit.Sizes), size => size == PieceTally.SlotBytesOf(states[1]));
            Assert.Equal(5, database.Counters.PublishedSequence);
            Assert.Equal(0, database.Counters.QueuedWrites);
            ExpectHeld(database, states[3], other);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Replace_cancelled_before_its_dispatch_gives_back_what_it_prepared_and_the_entity_keeps_its_state()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            var kept = PieceSchema.Of(2, name: 10);

            await pieces.InsertAsync(PieceSchema.Of(1, name: 10));
            await pieces.InsertAsync(kept);
            storage.Hold();

            var first = PieceSchema.Of(1, name: 26);
            var firstWrite = pieces.ReplaceAsync(first).AsTask();
            await storage.NextAsync();

            using var cancellation = new CancellationTokenSource();
            var withdrawn = PieceSchema.Of(2, name: 10, note: 300, data: 70_000);
            var cancelled = pieces.ReplaceAsync(withdrawn, cancellation.Token).AsTask();

            await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(first) + PieceTally.SlotBytesOf(withdrawn));
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            Assert.False(firstWrite.IsCompleted, "The replace held in the storage step completed before the cancelled replace ended.");

            var counters = database.Counters;

            Assert.Equal(PieceTally.SlotBytesOf(first), counters.Arena.PreparedBytes);
            Assert.Equal(1, counters.QueuedWrites);

            storage.ReleaseAll();
            await firstWrite.WaitAsync(Bound);

            Assert.Equal(10, pieces.Get(2L)?.Name.Length);
            Assert.Null(pieces.Get(2L)?.Data);
            ExpectHeld(database, first, kept);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_cancelled_while_it_is_being_prepared_is_withdrawn_when_its_preparation_ends_and_leaves_nothing()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage, allocator);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            // The first segment, which a value of 5 MiB outgrows.
            var stored = PieceSchema.Of(1, name: 10);

            await pieces.InsertAsync(stored);
            storage.Hold();

            var earlier = PieceSchema.Of(2, name: 10);
            var earlierWrite = pieces.InsertAsync(earlier).AsTask();
            await storage.NextAsync();

            using var cancellation = new CancellationTokenSource();
            using var held = HeldPreparation.Arm(allocator);
            var withdrawn = PieceSchema.Of(3, name: 10, data: 5 * 1024 * 1024);
            var cancelled = Task.Run(() => pieces.InsertAsync(withdrawn, cancellation.Token).AsTask());

            await held.HeldAsync();
            var cancelling = cancellation.CancelAsync();
            held.Release();
            await cancelling.WaitAsync(Bound);

            // Ended while the earlier write is still held in the storage step.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            Assert.False(earlierWrite.IsCompleted, "The write held in the storage step completed before the cancelled write ended.");

            var counters = database.Counters;

            Assert.Equal(PieceTally.SlotBytesOf(earlier), counters.Arena.PreparedBytes);
            Assert.Equal(1, counters.QueuedWrites);

            storage.ReleaseAll();
            await earlierWrite.WaitAsync(Bound);

            Assert.Null(pieces.Get(3L));
            Assert.DoesNotContain(storage.Commits.SelectMany(commit => commit.Sizes), size => size == PieceTally.SlotBytesOf(withdrawn));
            ExpectHeld(database, stored, earlier);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_cancelled_while_it_is_being_prepared_and_then_refused_by_the_arena_ends_without_waiting_for_the_earlier_write()
    {
        const int Large = 3 * 1024 * 1024;

        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage, allocator, new ArenaOptions { MemoryLimit = 3L * new ArenaOptions().SegmentSize });
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
            storage.Hold();

            // An earlier replace, prepared into the second segment and held in the storage step.
            var earlier = PieceSchema.Of(1, name: 11, data: Large);
            var earlierWrite = pieces.ReplaceAsync(earlier).AsTask();
            await storage.NextAsync();

            // An insert of 7 MiB, which needs the third segment, and which the arena refuses beside the
            // two states of entity 1.
            using var cancellation = new CancellationTokenSource();
            using var held = HeldPreparation.Arm(allocator);
            var cancelled = Task.Run(() => pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: 7 * 1024 * 1024), cancellation.Token).AsTask());

            await held.HeldAsync();
            var cancelling = cancellation.CancelAsync();
            held.Release();
            await cancelling.WaitAsync(Bound);

            var ended = await Task.WhenAny(cancelled, earlierWrite).WaitAsync(Bound);

            Assert.True(ended == cancelled, "The cancelled write waited for the earlier write, held in the storage step, to complete.");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

            var counters = database.Counters;

            Assert.Equal(PieceTally.SlotBytesOf(earlier), counters.Arena.PreparedBytes);
            Assert.Equal(1, counters.QueuedWrites);

            storage.ReleaseAll();
            Assert.True((await earlierWrite.WaitAsync(Bound)).Changed);

            Assert.Null(pieces.Get(2L));
            ExpectHeld(database, earlier);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    private static async Task<EngineDatabase> OpenAsync(HeldCommitStorage storage, CountingNativeAllocator allocator, ArenaOptions? arena = null) =>
        await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs
        {
            Arena = arena ?? new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize },
            Allocator = allocator,
            CommitStorage = storage,
        });

    // Entities 1 to 4 and 6 to 9, small: the first table of the primary index, of two buckets, holds
    // eight entries, so the preparation of an insert of entity 5 grows it (Storage.Hashing.IndexTable,
    // "The capacity policy").
    private static PieceSchema.Piece[] EightPieces() =>
        [.. new long[] { 1, 2, 3, 4, 6, 7, 8, 9 }.Select(id => PieceSchema.Of(id, name: 10))];

    private static async Task InsertEightAsync(ILatticeSet<PieceSchema.Piece> pieces)
    {
        foreach (var piece in EightPieces())
        {
            await pieces.InsertAsync(piece);
        }
    }

    // The next growth of the primary index cancels the token, inside the lock of the writes and on the
    // thread of the write that grows it; the task completes when it did.
    private static Task ArmCancellationAtTheNextGrowth(EngineDatabase database, ILatticeSet<PieceSchema.Piece> pieces, CancellationTokenSource cancellation)
    {
        var grown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        database.HoldNextGrowth(pieces.EntityType, () =>
        {
            cancellation.Cancel();
            grown.TrySetResult();
        });

        return grown.Task;
    }

    private static async Task<EngineDatabase> OpenAsync(HeldCommitStorage storage) =>
        await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

    // After a drain with no read in progress, the database holds these pieces alone.
    private static void ExpectHeld(EngineDatabase database, params PieceSchema.Piece[] pieces) =>
        PieceTally.Of(pieces).ExpectHeldBy(database);
}
