using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// After a write that failed validation or was refused, nothing of it is left in any counter (brief
// of Epic 1, sprint S06, the planner's answer to Q30): the live bytes, the class slack, the prepared
// and the retired bytes of the arena, the bytes of the primary index, the count of the table of root
// entries and the published sequence are as they were, and the arena holds what the entities hold.
// The segments are not among them: a segment the arena obtained for a write that failed may stay in
// its reserve, so Segments, ReservedSegments and FreeRegions may differ (brief of S05, "The size of
// an entity").
//
// By decision O19 an entity has no size limit, so no write is refused for its size. What is left
// of WriteRejected is the refusal for the memory of the arena, which the engine cannot be brought to
// here: until S09 gives the option, its arena gets half of the memory the runtime reports. A value
// above the bound of one value is ValidationFailed.
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

        // Validation by the root writer: in Note, text with a surrogate without its pair, after the
        // value of Name in chunks.
        await ExpectNothingLeftAsync(LatticeErrorEnum.ValidationFailed, () => pieces.InsertAsync(new PieceSchema.Piece { Id = 3, Name = new string('n', 70_000), Note = "a\uD800b" }));

        // Refused by the state of the database, each with values in chunks.
        await ExpectNothingLeftAsync(LatticeErrorEnum.DuplicateKey, () => pieces.InsertAsync(PieceSchema.Of(2, name: 70_000, data: 70_000)));
        await ExpectNothingLeftAsync(LatticeErrorEnum.NotFound, () => pieces.ReplaceAsync(PieceSchema.Of(404, name: 70_000)));
        await ExpectNothingLeftAsync(LatticeErrorEnum.RevisionConflict, () => pieces.ReplaceAsync(PieceSchema.Of(1, name: 70_000), stale));
        await ExpectNothingLeftAsync(LatticeErrorEnum.RevisionConflict, () => records.DeleteAsync(1L, stale));
        await ExpectNothingLeftAsync(LatticeErrorEnum.RevisionConflict, () => pieces.ReplaceAsync(PieceSchema.Of(404, name: 70_000), stale));

        // Refused as not stored yet, and by the check of its arguments.
        var holder = new LatticeRecord(holders.EntityType) { ["Id"] = 1L, ["Lines"] = new[] { new string('l', 70_000) } };
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

    [Fact]
    public async Task Write_of_a_value_above_the_bound_of_one_value_leaves_nothing_in_any_counter()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");
        var tally = new PieceTally();
        var kept = PieceSchema.Of(1, name: 70_000, data: 5_000);
        await pieces.InsertAsync(kept);
        tally.Written(kept);
        var before = CountersOf(database);

        // In Note, after the value of Name in chunks: as a new entity through the class, and by name
        // as a new state of one that exists.
        var text = ValueBound.TextOneByteAboveInUtf8;
        await ExpectValidationFailedAsync(() => pieces.InsertAsync(new PieceSchema.Piece { Id = 2, Name = new string('n', 70_000), Note = text }));
        var record = PieceSchema.RecordOf(records.EntityType, 1, name: 70_000);
        record["Note"] = text;
        await ExpectValidationFailedAsync(() => records.ReplaceAsync(record));

        async Task ExpectValidationFailedAsync(Func<ValueTask<LatticeWriteResult>> write)
        {
            var exception = await Assert.ThrowsAsync<LatticeException>(async () => await write());

            Assert.Equal(LatticeErrorEnum.ValidationFailed, exception.Error);
            Assert.Equal(before, CountersOf(database));
            tally.ExpectHeldBy(database);
        }
    }

    // Every counter of the engine but the segments of the arena.
    private static (long Live, long Slack, long Prepared, long Retired, long PrimaryIndex, int RootEntries, long Sequence) CountersOf(EngineDatabase database)
    {
        var counters = database.Counters;

        return (
            counters.Arena.LiveBytes,
            counters.Arena.ClassSlackBytes,
            counters.Arena.PreparedBytes,
            counters.Arena.RetiredBytes,
            counters.PrimaryIndexBytes,
            counters.RootEntries,
            counters.PublishedSequence);
    }
}
