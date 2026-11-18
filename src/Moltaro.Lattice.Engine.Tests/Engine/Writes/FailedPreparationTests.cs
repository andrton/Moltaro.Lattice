using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// A preparation that fails releases everything it reserved, shown by the counters, and the instance
// stays Ready (brief of Epic 1, sprint S09, Acceptance and the answer of the planner on the fault
// points: shown by real failures of a preparation). Of the three the planner names, a refusal of the
// arena is stated by RefusedWriteTests and a cancelled write by DispatchTests; this is the third, a
// failure while the values are encoded. No value reaches the writer of the engine that it refuses
// while it encodes: the validation of the core refuses every such value first (Entities.RootValues,
// Storage.RootWriter). What fails there is the native allocator, met inside a Set… member of the
// writer when the arena needs a new segment for a chunk of the value; its OutOfMemoryException passes
// through after the member has freed what it took (Storage.RootWriter). The database is opened with an
// allocator that can be made to fail its next request (CountingNativeAllocator, exception X3). A
// replace of an entity that exists and an insert of a new one each fail so: the call ends in that
// exception, the entity keeps its state or stays absent, the counters equal the tally after a drain,
// no write is left among those admitted, the instance is Ready, and the same writes succeed once the
// allocator works again.
public sealed class FailedPreparationTests
{
    private const int Chunked = 5 * 1024 * 1024;

    private static readonly ArenaOptions Arena = new() { MemoryLimit = 64L * new ArenaOptions().SegmentSize };

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Preparation_that_fails_while_it_encodes_a_value_releases_everything_and_leaves_the_instance_Ready()
    {
        var allocator = new CountingNativeAllocator();
        await using var database = await EngineMaster.OpenAsync(PieceSchema.Schema, Arena, allocator);
        var pieces = database.Set<PieceSchema.Piece>();
        var tally = new PieceTally();

        // One small piece, in the first segment. A value of 5 MiB needs a second one.
        var small = PieceSchema.Of(1, name: 10);
        await pieces.InsertAsync(small);
        tally.Written(small);

        allocator.FailNextAllocation = true;
        await Assert.ThrowsAsync<OutOfMemoryException>(() => pieces.ReplaceAsync(PieceSchema.Of(1, name: 20, data: Chunked)).AsTask().WaitAsync(Bound));
        ExpectAsBefore();
        Assert.Equal(10, pieces.Get(1L)?.Name.Length);

        allocator.FailNextAllocation = true;
        await Assert.ThrowsAsync<OutOfMemoryException>(() => pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: Chunked)).AsTask().WaitAsync(Bound));
        ExpectAsBefore();
        Assert.Null(pieces.Get(2L));

        // The same writes, once the allocator works again.
        foreach (var piece in new[] { PieceSchema.Of(1, name: 20, data: Chunked), PieceSchema.Of(2, name: 10, data: Chunked) })
        {
            await pieces.UpsertAsync(piece);
            tally.Written(piece);
        }

        ExpectAsBefore();
        Assert.Equal(3, database.Counters.PublishedSequence);

        void ExpectAsBefore()
        {
            Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
            Assert.Equal(0, database.Counters.QueuedWrites);
            tally.ExpectHeldBy(database);
        }
    }
}
