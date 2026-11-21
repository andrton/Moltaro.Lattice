using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.TestSupport.Enumeration;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Enumeration;

// An enumeration holds no gate and no claim between two steps, and nothing of the protection of
// reads: each step enters it and leaves it (brief of Epic 1, sprint S08, "Nothing is held between two
// steps"; arena protocol, "Deferred reclamation, 16 November 2026"). So:
// - An enumeration abandoned halfway without being disposed leaves nothing behind: once its entities
//   are deleted, after a drain the table of root entries holds none of them, nothing is retired,
//   neither in the arena nor of the primary index, which grew while the enumeration stood, and the
//   counters equal the tally (PieceTally). The abandoned enumerator stays reachable to the end, so no
//   finalizer can stand in for what it would have to release.
// - A write that needs memory which only a read in progress could keep retired does not wait for an
//   enumeration that stands between two steps. The database has an arena of two segments of 8 MiB
//   (exception X3), and a piece holds a Binary value of 3 MiB, as in RefusedWriteTests: the second
//   replace of it fits only once the state the first replaced is freed, and a write that has to wait
//   for reads for that runs the switch of the engine that tells it (HoldNextRefusedWriteWait).
// Every form of an enumeration; every wait is bounded.
public sealed class NothingHeldBetweenStepsTests
{
    private const int Pieces = 100;

    private const int Large = 3 * 1024 * 1024;

    private static readonly ArenaOptions TwoSegments = new() { MemoryLimit = 2L * new ArenaOptions().SegmentSize };

    public static IEnumerable<TheoryDataRow<string>> Forms => EnumerationForms.All.Select(form => new TheoryDataRow<string>(form));

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Enumeration_abandoned_halfway_without_being_disposed_leaves_nothing_behind_once_its_entities_are_deleted(string form)
    {
        var database = await PieceSchema.OpenAsync();

        try
        {
            var pieces = database.Set<PieceSchema.Piece>();
            var records = database.Set("Piece");
            var tally = new PieceTally();

            for (var id = 1L; id <= Pieces; id++)
            {
                tally.Written(await InsertedAsync(pieces, PieceSchema.Of(id, name: 100, note: 1_000)));
            }

            var steps = EnumerationForms.Of(form, pieces, records, piece => piece.Id, record => record.Get<long>("Id")).GetAsyncEnumerator();

            for (var step = 0; step < Pieces / 2; step++)
            {
                Assert.True(await steps.MoveNextAsync());
            }

            // While it stands, the index grows, and then every entity is deleted.
            for (var id = Pieces + 1L; id <= 5 * Pieces; id++)
            {
                tally.Written(await InsertedAsync(pieces, PieceSchema.Of(id, name: 100)));
            }

            for (var id = 1L; id <= 5 * Pieces; id++)
            {
                Assert.True((await pieces.DeleteAsync(id)).Changed);
                tally.Deleted(id);
            }

            tally.ExpectHeldBy(database);
            GC.KeepAlive(steps);
        }
        finally
        {
            // An enumeration that held something between two steps would hold back the close too.
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Write_that_needs_memory_retired_while_an_enumeration_stands_between_two_steps_does_not_wait_for_it(string form)
    {
        await using var database = await EngineMaster.OpenAsync(PieceSchema.Schema, TwoSegments, new NativeAllocator());
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");

        await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
        await pieces.InsertAsync(PieceSchema.Of(2, name: 10));

        await using var steps = EnumerationForms.Of(form, pieces, records, piece => piece.Id, record => record.Get<long>("Id")).GetAsyncEnumerator();
        Assert.True(await steps.MoveNextAsync());
        var returned = new List<long> { steps.Current };

        // The first replace retires the first state, which stays retired; the second fits only once
        // it is freed.
        await Task.Run(() => pieces.ReplaceAsync(PieceSchema.Of(1, name: 11, data: Large)).AsTask()).WaitAsync(Bound);
        Assert.True(database.Counters.Arena.RetiredBytes > 0, "The replaced state was freed before the write that needs its memory.");

        var waits = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        database.HoldNextRefusedWriteWait(() => waits.TrySetResult());

        var write = Task.Run(() => pieces.ReplaceAsync(PieceSchema.Of(1, name: 12, data: Large)).AsTask());

        await Task.WhenAny(write, waits.Task).WaitAsync(Bound);

        Assert.False(waits.Task.IsCompleted, "The write waited for the reads while an enumeration stood between two steps.");
        Assert.True((await write.WaitAsync(Bound)).Changed);
        Assert.Equal(12, pieces.Get(1L)?.Name.Length);

        // The enumeration goes on to its end, and has returned each piece once.
        while (await steps.MoveNextAsync())
        {
            returned.Add(steps.Current);
        }

        Assert.Equal([1L, 2L], returned.Order());
    }

    private static async Task<PieceSchema.Piece> InsertedAsync(ILatticeSet<PieceSchema.Piece> pieces, PieceSchema.Piece piece)
    {
        await pieces.InsertAsync(piece);

        return piece;
    }
}
