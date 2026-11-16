using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// A write the arena refuses frees what no read in progress can reach and tries again, then waits for
// the reads that still hold retired memory, frees and tries once more, and only then is refused
// (brief of Epic 1, sprint S08, "Tests the sprint breaks, part 1": on a refusal of the arena a write
// drains and retries, and waits for the reads that still hold retired memory before it is refused;
// the wait honors the cancellation of the write's token). The database is opened with an arena of two
// segments of the default size, 8 MiB (EngineDatabase.Create with arena options, exception X3), and
// a piece holds a Binary value of 3 MiB: two states of it fit, three do not. A replace retires the
// state it replaced, which stays retired: a look of the writer comes only after a segment of retired
// bytes or a table's worth of retired things. So:
// - A replace whose new state fits only once the retired one is freed, which no read holds, succeeds.
// - When a read held inside the protection (HoldNextRead) holds the retired state, the replace waits:
//   it does not end while the read is held, its token cancels the wait and the write changes nothing,
//   and a replace that waits again succeeds once the read has left.
// - An entity that does not fit even after everything retired is freed is WriteRejected, and nothing
//   of it is left.
// A test cannot see that a write began to wait, since no switch on the side of the writer tells it;
// so it gives a write that would wrongly be refused a window of time to end, and states that it did
// not. A correct engine passes however long that window is. Every other wait is bounded.
public sealed class RefusedWriteTests
{
    private const int Large = 3 * 1024 * 1024;

    private static readonly int Segment = new ArenaOptions().SegmentSize;

    private static readonly ArenaOptions TwoSegments = new() { MemoryLimit = 2L * Segment };

    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Replace_that_the_arena_holds_only_once_retired_memory_no_read_holds_is_freed_succeeds()
    {
        await using var database = await EngineMaster.OpenAsync(PieceSchema.Schema, TwoSegments, new NativeAllocator());
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

        // The third state fits only once the first is freed: the replace waits, and its token ends the
        // wait. A cancelled write changes nothing.
        using var cancellation = new CancellationTokenSource();
        var cancelled = Task.Run(() => pieces.ReplaceAsync(PieceSchema.Of(1, name: 12, data: Large), cancellation.Token).AsTask());

        Assert.False(await EndsWithinWindowAsync(cancelled), "The replace ended while the read that holds the memory it needs was in progress.");

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
        Assert.Equal(11, pieces.Get(1L)?.Name.Length);

        // A replace that waits again succeeds once the held read has left, which returns the state it
        // found or the one that replaced it.
        var waiting = Task.Run(() => pieces.ReplaceAsync(PieceSchema.Of(1, name: 13, data: Large)).AsTask());

        Assert.False(await EndsWithinWindowAsync(waiting), "The replace ended while the read that holds the memory it needs was in progress.");
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
        var refused = await Assert.ThrowsAsync<LatticeException>(async () => await pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: 2 * Large)));

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Null(pieces.Get(2L));
        ExpectHeld(database, PieceSchema.Of(1, name: 11, data: Large));
    }

    // Whether the write ends within the window: a write that waits for a read the test holds does not.
    private static async Task<bool> EndsWithinWindowAsync(Task write) =>
        await Task.WhenAny(write, Task.Delay(Window)) == write;

    // After a drain with no read in progress, the database holds this piece alone.
    private static void ExpectHeld(EngineDatabase database, PieceSchema.Piece piece)
    {
        var tally = new PieceTally();

        tally.Written(piece);
        tally.ExpectHeldBy(database);
    }
}
