using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// By the planner's answer to Q30 (brief of Epic 1, sprint S06, Acceptance): when every write has
// returned and no read is in progress, PreparedBytes and RetiredBytes of the arena are 0, and the
// counters of the arena equal an independent tally of what the entities hold. A block is counted in
// exactly one place: prepared from its allocation until its write is published, then live, then
// retired while what still refers to it is kept, until it is freed. The tally is PieceTally, from the
// rules of S05.
public sealed class ArenaCounterTests
{
    [Fact]
    public async Task Arena_holds_what_the_entities_hold_after_every_write()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");
        var tally = new PieceTally();

        tally.ExpectHeldBy(database);

        // Inserted and upserted with values inline, in a block of their own, in one chunk, in two
        // chunks, empty and null (brief of S05, "Where a value goes"), through the class.
        await WrittenAsync(pieces.InsertAsync, PieceSchema.Of(1, name: 10));
        await WrittenAsync(pieces.InsertAsync, PieceSchema.Of(2, name: 100, note: 5_000, data: 64));
        await WrittenAsync(pieces.UpsertAsync, PieceSchema.Of(3, name: 70_000, note: 0, data: 4_096));

        // Replaced with values of other sizes, also through a condition on the revision.
        await WrittenAsync(pieces.ReplaceAsync, PieceSchema.Of(1, name: 4_097, note: 65, data: 0));
        Assert.True(pieces.TryGet(2L, out _, out var revision));
        await WrittenAsync((piece, cancellationToken) => pieces.ReplaceAsync(piece, revision, cancellationToken), PieceSchema.Of(2, name: 1));
        await WrittenAsync(pieces.UpsertAsync, PieceSchema.Of(3, name: 64, note: 64, data: 64));

        // By name.
        await records.InsertAsync(PieceSchema.RecordOf(records.EntityType, 4, name: 200, data: 100_000));
        tally.Written(PieceSchema.Of(4, name: 200, data: 100_000));
        tally.ExpectHeldBy(database);

        await records.ReplaceAsync(PieceSchema.RecordOf(records.EntityType, 4, name: 3));
        tally.Written(PieceSchema.Of(4, name: 3));
        tally.ExpectHeldBy(database);

        // Deleted, deleted again, inserted again.
        await pieces.DeleteAsync(2L);
        tally.Deleted(2);
        tally.ExpectHeldBy(database);

        await pieces.DeleteAsync(2L);
        tally.ExpectHeldBy(database);

        await WrittenAsync(pieces.InsertAsync, PieceSchema.Of(2, name: 5));

        Assert.True(records.TryGet(4L, out _, out var recordRevision));
        await records.DeleteAsync(4L, recordRevision);
        tally.Deleted(4);
        tally.ExpectHeldBy(database);

        async Task WrittenAsync(Func<PieceSchema.Piece, CancellationToken, ValueTask<LatticeWriteResult>> write, PieceSchema.Piece piece)
        {
            await write(piece, CancellationToken.None);
            tally.Written(piece);
            tally.ExpectHeldBy(database);
        }
    }

    [Fact]
    public async Task Nothing_is_prepared_or_retired_once_replaces_raced_by_reads_have_returned()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");
        var tally = new PieceTally();
        var last = PieceSchema.Of(1, name: 70_000, note: 1_000);
        await pieces.InsertAsync(last);

        // Two readers through the class and two by name read the piece while it is replaced 200
        // times, so that the blocks of a replaced state are retired while a read may still copy them.
        await ReadersAndWriter.RunAsync(
            4,
            reader => reader % 2 == 0 ? () => Assert.NotNull(pieces.Get(1L)) : () => Assert.NotNull(records.Get(1L)),
            async () =>
            {
                for (var write = 0; write < 200; write++)
                {
                    last = write % 2 == 0 ? PieceSchema.Of(1, name: 10, data: 10) : PieceSchema.Of(1, name: 70_000, note: 1_000, data: 5_000);
                    await pieces.ReplaceAsync(last);
                }
            });

        tally.Written(last);
        tally.ExpectHeldBy(database);
    }
}
