using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Writes;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// Cancellation up to the dispatch (brief of Epic 1, sprint S09, Acceptance and the answers of the
// planner on the dispatch and the counters; decision "Cancellation at the commit boundary"). The
// dispatch of a write is the call of ICommitStorage.CommitAsync for the group the commit stage took it
// into. The storage of the test holds a commit until the test releases it (HeldCommitStorage), so the
// commit stage stays busy while the test cancels a write that waits for it. So:
// - A write cancelled before its dispatch is withdrawn: its call ends in OperationCanceledException
//   while the commit stage is still busy, no group ever holds it, and nothing of it is left: neither
//   its prepared capacity (PreparedBytes) nor its place among the writes admitted (QueuedWrites), and
//   after a drain the counters equal the tally of the writes that ran.
// - From the dispatch on the call returns the real outcome, and its token is not looked at: a write
//   whose token is cancelled while the storage holds its group is published.
// - A write admitted behind an earlier write of the same entity waits for its turn and holds nothing
//   in the engine meanwhile, while a write of another entity admitted after it is prepared at once.
//   Cancelled while it waits, it has no effect, and the later writes of that entity run in the order
//   of their calls.
// Each write is known by its encoded size, which differs from every other's (PieceTally.SlotBytesOf).
// Every wait is bounded; the storage releases every commit when a test ends.
public sealed class DispatchTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact(Skip = NotImplemented.WriteCoordinator)]
    public async Task Write_cancelled_before_its_dispatch_is_withdrawn_ends_in_OperationCanceledException_and_leaves_nothing()
    {
        var storage = new HeldCommitStorage();
        await using var database = await OpenAsync(storage);
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
        }
    }

    [Fact(Skip = NotImplemented.WriteCoordinator)]
    public async Task Write_whose_token_is_cancelled_after_its_dispatch_returns_its_real_outcome()
    {
        var storage = new HeldCommitStorage();
        await using var database = await OpenAsync(storage);
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
        }
    }

    [Fact(Skip = NotImplemented.WriteCoordinator)]
    public async Task Write_cancelled_while_it_waits_behind_an_earlier_write_of_its_entity_has_no_effect_and_the_later_writes_of_that_entity_run_in_order()
    {
        var storage = new HeldCommitStorage(holding: false);
        await using var database = await OpenAsync(storage);
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
        }
    }

    private static async Task<EngineDatabase> OpenAsync(HeldCommitStorage storage) =>
        await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

    // After a drain with no read in progress, the database holds these pieces alone.
    private static void ExpectHeld(EngineDatabase database, params PieceSchema.Piece[] pieces) =>
        PieceTally.Of(pieces).ExpectHeldBy(database);
}
