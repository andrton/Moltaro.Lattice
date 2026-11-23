using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// After a write that failed validation or was refused, nothing of it is left in any counter (brief
// of Epic 1, sprint S06, the planner's answer to Q30): the live bytes, the class slack, the prepared
// and the retired bytes of the arena, the bytes of the primary index, retired and being built
// included, the count of the table of root entries and the published sequence are as they were, and
// the arena holds what the entities hold. The counters are compared after a drain (brief of S08,
// "Counters and the drain, part 1"), so that what the replace before them retired is not taken for
// something a failed write left.
// The segments are not among them: a segment the arena obtained for a write that failed may stay in
// its reserve, so Segments, ReservedSegments and FreeRegions may differ (brief of S05, "The size of
// an entity").
//
// By decision O19 an entity has no size limit, so no write is refused for its size. What is left
// of WriteRejected is the refusal for the memory budget of the database, which this database, with the
// default budget of half of the memory the runtime reports, does not come near: the tests of the
// memory budget state it on a small arena (MemoryBudget.RefusedWriteTests), with the same rule that
// nothing of a refused write is left. A value above the bound of one value is ValidationFailed.
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

        // Refused as not stored yet, and by the check of its arguments.
        var holder = new LatticeRecord(holders.EntityType) { ["Id"] = 1L, ["Lines"] = new Dictionary<string, string> { ["line"] = new string('l', 70_000) } };
        await Assert.ThrowsAsync<NotSupportedException>(async () => await holders.InsertAsync(holder));
        ExpectAsBefore();
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
