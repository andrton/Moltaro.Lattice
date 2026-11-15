using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// PublishedSequence is the sequence of the last published position: 0 for a database without a
// write and 1 after the first (brief of Epic 1, sprint S06, the planner's answer to Q38). Every write
// the engine accepts and executes is published at a position of its own, whether it changed the
// database or not, so the sequence grows by one for every write, also for a delete that deleted
// nothing (the owner's answer to Q39). A read publishes nothing. What an application sees of this,
// the position, is stated by PositionTests of Moltaro.Lattice.Tests; that a write that fails
// publishes nothing, by FailedWriteTests.
public sealed class PublishedSequenceTests
{
    [Fact(Skip = NotImplemented.EngineDatabase)]
    public async Task Database_without_a_write_has_sequence_0_and_after_its_first_write_1()
    {
        await using var database = await PieceSchema.OpenAsync();

        Assert.Equal(0, database.Counters.PublishedSequence);

        await database.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 3));

        Assert.Equal(1, database.Counters.PublishedSequence);
    }

    [Fact(Skip = NotImplemented.EngineDatabase)]
    public async Task First_write_that_deletes_nothing_has_sequence_1()
    {
        await using var database = await PieceSchema.OpenAsync();
        var before = database.Position;

        var deleted = await database.Set<PieceSchema.Piece>().DeleteAsync(1L);

        Assert.False(deleted.Changed);
        Assert.Equal(1, database.Counters.PublishedSequence);
        Assert.NotEqual(before, database.Position);
    }

    [Fact(Skip = NotImplemented.EngineDatabase)]
    public async Task Every_write_publishes_the_next_sequence_also_one_that_changed_nothing()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");
        var session = new LatticeSession();
        var expected = 0L;

        // Through the class, without and with a session and a condition on the revision.
        await NextAsync(pieces.InsertAsync(PieceSchema.Of(1, name: 3)));
        await NextAsync(pieces.InsertAsync(PieceSchema.Of(2, name: 3), session));
        await NextAsync(pieces.ReplaceAsync(PieceSchema.Of(1, name: 4)));
        await NextAsync(pieces.ReplaceAsync(PieceSchema.Of(1, name: 5), session));
        Assert.True(pieces.TryGet(1L, out _, out var revision));
        await NextAsync(pieces.ReplaceAsync(PieceSchema.Of(1, name: 6), revision));
        await NextAsync(pieces.UpsertAsync(PieceSchema.Of(3, name: 3)));
        await NextAsync(pieces.UpsertAsync(PieceSchema.Of(3, name: 4), session));
        Assert.True(pieces.TryGet(3L, out _, out revision));
        await NextAsync(pieces.DeleteAsync(3L, revision));
        await NextAsync(pieces.DeleteAsync(2L, session));

        // Deletes of absent keys, also the same key twice.
        await NextAsync(pieces.DeleteAsync(404L));
        await NextAsync(pieces.DeleteAsync(404L));
        await NextAsync(pieces.DeleteAsync(405L, session));

        // By name.
        await NextAsync(records.InsertAsync(PieceSchema.RecordOf(records.EntityType, 4, name: 3)));
        await NextAsync(records.ReplaceAsync(PieceSchema.RecordOf(records.EntityType, 4, name: 4), session));
        await NextAsync(records.UpsertAsync(PieceSchema.RecordOf(records.EntityType, 5, name: 3)));
        await NextAsync(records.DeleteAsync(4L));
        await NextAsync(records.DeleteAsync(4L));

        async Task NextAsync(ValueTask<LatticeWriteResult> write)
        {
            var result = await write;
            expected++;

            Assert.Equal(expected, database.Counters.PublishedSequence);
            Assert.Equal(result.Position, database.Position);
        }
    }

    [Fact(Skip = NotImplemented.EngineDatabase)]
    public async Task Reads_publish_nothing()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");
        await pieces.InsertAsync(PieceSchema.Of(1, name: 3));
        var position = database.Position;

        Assert.NotNull(pieces.Get(1L));
        Assert.Null(pieces.Get(404L));
        Assert.True(records.TryGet(1L, out _));
        Assert.Single(pieces.Enumerate());
        Assert.Single(records.Enumerate());
        Assert.True((await pieces.GetAsync(1L, new LatticeSession(position))).Found);
        Assert.Single(await records.EnumerateAsync(new LatticeSession(position)).ToListAsync());
        await database.WaitForPositionAsync(position);

        Assert.Equal(1, database.Counters.PublishedSequence);
        Assert.Equal(position, database.Position);
    }
}
