using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// A failed validation or refused write leaves the arena's live, slack, prepared and retired bytes,
// the bytes of the primary index, the root entry count and the published sequence as they were.
// The test drains retired memory before reading the counters so an earlier replacement contributes
// nothing to them. A failed write may have obtained a segment that the arena kept in its reserve,
// so Segments, ReservedSegments and FreeRegions can change (docs/design/arena-protocol.md).
//
// The memory budget covers the whole database. A value above the bound of one value is
// ValidationFailed. A write refused by the budget is WriteRejected, an outcome that
// MemoryBudget.RefusedWriteTests exercises with a small arena.
public sealed class FailedWriteTests
{
    [Fact]
    public async Task Write_that_fails_validation_or_is_refused_leaves_nothing_in_any_counter()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");
        var holders = database.Set("Holder");
        var tally = new PieceTally();

        foreach (var piece in new[] { PieceSchema.Of(1, name: 70_000, note: 100), PieceSchema.Of(2, name: 10, data: 5_000) })
        {
            await pieces.InsertAsync(piece);
            tally.Written(piece);
        }

        Assert.True(pieces.TryGet(1L, out _, out var stale));
        var current = PieceSchema.Of(1, name: 3);
        await pieces.ReplaceAsync(current);
        tally.Written(current);
        var before = CountersOf(database);

        // Validation: a required value is missing, through the class and by name.
        await ExpectNothingLeftAsync(LatticeErrorEnum.ValidationFailed, () => pieces.InsertAsync(new PieceSchema.Piece { Id = 3, Name = null! }));
        var nameless = PieceSchema.RecordOf(records.EntityType, 3, name: 1, data: 70_000);
        nameless["Name"] = null;
        await ExpectNothingLeftAsync(LatticeErrorEnum.ValidationFailed, () => records.UpsertAsync(nameless));

        // Validation: in Note, text with a surrogate without its pair, and a value above the bound of
        // one value, after the value of Name in chunks. As a new entity through the class, and the
        // second also by name as a new state of one that exists.
        await ExpectNothingLeftAsync(LatticeErrorEnum.ValidationFailed, () => pieces.InsertAsync(new PieceSchema.Piece { Id = 3, Name = new string('n', 70_000), Note = "a\uD800b" }));
        await ExpectNothingLeftAsync(LatticeErrorEnum.ValidationFailed, () => pieces.InsertAsync(new PieceSchema.Piece { Id = 3, Name = new string('n', 70_000), Note = ValueBound.TextOneByteAboveInUtf8 }));
        var above = PieceSchema.RecordOf(records.EntityType, 1, name: 70_000);
        above["Note"] = ValueBound.TextOneByteAboveInUtf8;
        await ExpectNothingLeftAsync(LatticeErrorEnum.ValidationFailed, () => records.ReplaceAsync(above));

        // Refused by the state of the database, each with values in chunks.
        await ExpectNothingLeftAsync(LatticeErrorEnum.DuplicateKey, () => pieces.InsertAsync(PieceSchema.Of(2, name: 70_000, data: 70_000)));
        await ExpectNothingLeftAsync(LatticeErrorEnum.NotFound, () => pieces.ReplaceAsync(PieceSchema.Of(404, name: 70_000)));
        await ExpectNothingLeftAsync(LatticeErrorEnum.RevisionConflict, () => pieces.ReplaceAsync(PieceSchema.Of(1, name: 70_000), stale));
        await ExpectNothingLeftAsync(LatticeErrorEnum.RevisionConflict, () => records.DeleteAsync(1L, stale));
        await ExpectNothingLeftAsync(LatticeErrorEnum.RevisionConflict, () => pieces.ReplaceAsync(PieceSchema.Of(404, name: 70_000), stale));

        // A late entry in a collection has malformed text.
        var holder = new LatticeRecord(holders.EntityType) { ["Id"] = 1L, ["Lines"] = new Dictionary<string, string> { ["line"] = new string('l', 70_000) } };
        holder.Get<Dictionary<string, string>>("Lines")!.Add("second", "\uD800");
        await ExpectNothingLeftAsync(LatticeErrorEnum.ValidationFailed, () => holders.InsertAsync(holder));

        // Refused by the check of the key argument.
        await Assert.ThrowsAsync<ArgumentException>(async () => await pieces.DeleteAsync("one"));
        ExpectAsBefore();

        async Task ExpectNothingLeftAsync(LatticeErrorEnum error, Func<ValueTask<LatticeWriteResult>> write)
        {
            var exception = await Assert.ThrowsAsync<LatticeException>(async () => await write());

            Assert.Equal(error, exception.Error);
            ExpectAsBefore();
        }

        void ExpectAsBefore()
        {
            Assert.Equal(before, CountersOf(database));
            tally.ExpectHeldBy(database);
        }
    }

    // Every counter of the engine but the segments of the arena, after a drain.
    private static (long Live, long Slack, long Prepared, long Retired, long PrimaryIndex, long RetiredIndex, long ReplacementIndex, int RootEntries, long Sequence) CountersOf(EngineDatabase database)
    {
        database.DrainRetired();

        var counters = database.Counters;

        return (
            counters.Arena.LiveBytes,
            counters.Arena.ClassSlackBytes,
            counters.Arena.PreparedBytes,
            counters.Arena.RetiredBytes,
            counters.PrimaryIndexBytes,
            counters.RetiredIndexBytes,
            counters.ReplacementIndexBytes,
            counters.RootEntries,
            counters.PublishedSequence);
    }
}
