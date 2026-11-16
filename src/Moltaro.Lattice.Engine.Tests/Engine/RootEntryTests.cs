namespace Moltaro.Lattice.Engine.Tests.Engine;

// RootEntries is the count of the table of root entries, the slots that hold the lifetime of a root
// (EngineCounters). An insert takes an entry for its entity, and a replace keeps it: the entry stays
// where it is while the root it names changes (RootEntry.Root). After the deletes have returned and
// a drain with no read in progress, the table holds no entry of a deleted entity (brief of Epic 1,
// sprint S06, the planner's answer to Q32, as amended on 16 November 2026): the entry of a deleted
// root is retired and released once the reads that could see it have left, which a drain with no
// read in progress makes sure of (EngineDatabase.DrainRetired).
public sealed class RootEntryTests
{
    [Fact]
    public async Task Table_of_root_entries_holds_one_entry_for_every_entity_and_none_of_a_deleted_one_after_a_drain()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");

        Assert.Equal(0, database.Counters.RootEntries);

        for (var id = 1L; id <= 10; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, name: 10));
        }

        Assert.Equal(10, database.Counters.RootEntries);

        // A replace keeps the entry, through the class and by name, and so do reads.
        await pieces.ReplaceAsync(PieceSchema.Of(1, name: 5_000));
        await pieces.UpsertAsync(PieceSchema.Of(2, name: 70_000));
        await records.ReplaceAsync(PieceSchema.RecordOf(records.EntityType, 3, name: 1));
        Assert.NotNull(pieces.Get(1L));
        Assert.Null(pieces.Get(404L));
        Assert.Equal(10, pieces.Enumerate().Count());

        Assert.Equal(10, database.Counters.RootEntries);

        // Deletes release theirs once a drain finds no read in progress, a delete of an absent key
        // releases nothing.
        await pieces.DeleteAsync(1L);
        await pieces.DeleteAsync(2L);
        await records.DeleteAsync(3L);
        Assert.True(records.TryGet(4L, out _, out var revision));
        await records.DeleteAsync(4L, revision);
        await pieces.DeleteAsync(1L);
        database.DrainRetired();

        Assert.Equal(6, database.Counters.RootEntries);

        // An entity inserted again, by an insert or an upsert, takes an entry again.
        await pieces.InsertAsync(PieceSchema.Of(1, name: 10));
        await records.UpsertAsync(PieceSchema.RecordOf(records.EntityType, 2, name: 10));

        Assert.Equal(8, database.Counters.RootEntries);
    }

    [Fact]
    public async Task No_entry_of_a_deleted_entity_is_left_once_deletes_raced_by_reads_have_returned_and_a_drain()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");
        const int Keys = 16;
        const int Rounds = 100;

        for (var id = 1L; id <= Keys; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, name: 5_000));
        }

        // Readers read every key, through the class and by name, while every entity is deleted and
        // inserted again, round by round, and in the last round deleted for good when its key is
        // even.
        await ReadersAndWriter.RunAsync(
            4,
            reader =>
            {
                var next = reader;
                return reader % 2 == 0
                    ? () => _ = pieces.Get(1L + (next++ % Keys))
                    : () => _ = records.Get(1L + (next++ % Keys));
            },
            async () =>
            {
                for (var round = 1; round <= Rounds; round++)
                {
                    for (var id = 1L; id <= Keys; id++)
                    {
                        await pieces.DeleteAsync(id);

                        if (round < Rounds || id % 2 == 1)
                        {
                            await pieces.InsertAsync(PieceSchema.Of(id, name: 5_000));
                        }
                    }
                }
            });

        // The readers have ended.
        database.DrainRetired();

        Assert.Equal(Keys / 2, database.Counters.RootEntries);

        // Nor is anything of the deleted roots left in the arena.
        var tally = new PieceTally();
        for (var id = 1L; id <= Keys; id += 2)
        {
            tally.Written(PieceSchema.Of(id, name: 5_000));
        }

        tally.ExpectHeldBy(database);
    }
}
