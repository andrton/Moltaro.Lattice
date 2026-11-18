using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.TestSupport.Skips;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The one writer frees what it retired on the path of its own writes, without a drain being called
// (arena protocol, "Deferred reclamation, 16 November 2026"; brief of Epic 1, sprint S08, "Added by
// the planner on 16 November 2026"). It does not look at the readers after every write: it looks
// when the things retired since its last look reach the number of slots of the epoch table, or their
// bytes reach one segment of the arena, and a look with no read in progress frees everything
// retired before it. So with no read in progress, during a run of replaces RetiredBytes falls, and
// after every write it stays below what a table's worth of retired roots holds, or below a segment and
// one root. The thresholds are derived from the table and the arena, not options: the test reads the
// number of slots from an epoch table of this process, which depends on the processors alone
// (ReadEpochs.SlotCount), and the segment from the default options of the arena (ArenaOptions).
// Nothing reads while the writes run, and nothing sleeps.
public sealed class PublishingLookTests
{
    // The first number of a state, so that every number of the test has as many digits and every root
    // takes the same bytes.
    private const long Numbers = 1_000_000;

    // A Name of one MiB: four roots of it reach one segment long before the slots are reached.
    private const int LargeName = 1024 * 1024;

    // One segment of the arena, as the database opens it.
    private static readonly int Segment = new ArenaOptions().SegmentSize;

    [Fact]
    public async Task Replaces_that_retire_as_many_roots_as_the_epoch_table_has_slots_free_them_without_a_drain()
    {
        var slots = new ReadEpochs(new object()).SlotCount;

        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, Numbers));
        database.DrainRetired();
        var root = HeldBytes(database);

        var fell = false;
        var previous = 0L;

        for (var write = 1; write <= 2 * slots; write++)
        {
            await stamps.ReplaceAsync(StampSchema.StampOf(1, Numbers + write));
            var retired = database.Counters.Arena.RetiredBytes;

            Assert.True(retired < slots * root, $"After write {write} the arena holds {retired} retired bytes, a table's worth of roots of {root} bytes or more.");

            fell |= retired < previous;
            previous = retired;
        }

        Assert.True(fell, $"RetiredBytes never fell during {2 * slots} replaces without a drain.");
    }

    [Fact]
    public async Task Replaces_that_retire_one_segment_of_bytes_free_them_without_a_drain()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, name: LargeName));
        database.DrainRetired();
        var root = HeldBytes(database);
        var writes = (int)(3 * Segment / root) + 1;

        var fell = false;
        var previous = 0L;

        for (var write = 1; write <= writes; write++)
        {
            await pieces.ReplaceAsync(PieceSchema.Of(1, name: LargeName));
            var retired = database.Counters.Arena.RetiredBytes;

            Assert.True(retired < Segment + root, $"After write {write} the arena holds {retired} retired bytes, a segment and a root of {root} bytes or more.");

            fell |= retired < previous;
            previous = retired;
        }

        Assert.True(fell, $"RetiredBytes never fell during {writes} replaces of roots of {root} bytes without a drain.");
    }

    // What waits between two looks is bounded by the two thresholds beside what reads hold, until the next
    // operation of the writer (docs/design/arena-protocol.md, "Deferred reclamation, 16 November 2026"):
    // a look that a read kept from freeing leaves retired roots behind, and while any remain, a later
    // published write of any kind looks again, also one that retires nothing. A read is held (HeldRead)
    // while as many replaces as the epoch table has slots make a look; once it has left, the delete of a
    // key that is absent, which retires nothing, frees all of them.
    [Fact(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    public async Task Roots_a_held_read_kept_from_a_look_are_freed_by_the_next_write_after_it_left_also_one_that_retires_nothing()
    {
        var slots = new ReadEpochs(new object()).SlotCount;

        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, Numbers));
        await stamps.InsertAsync(StampSchema.StampOf(2, Numbers));
        database.DrainRetired();

        using var held = await HeldRead.StartAsync(database, () => StampSchema.NumberOf(2, stamps.Get(2L)));

        for (var write = 1; write <= slots; write++)
        {
            await stamps.ReplaceAsync(StampSchema.StampOf(1, Numbers + write));
        }

        Assert.True(database.Counters.Arena.RetiredBytes > 0, "The look the replaces made freed what a held read could reach.");
        Assert.Equal(Numbers, await held.ReleaseAsync());

        var nothingDeleted = await stamps.DeleteAsync(404L);

        Assert.False(nothingDeleted.Changed);
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    // The live bytes and the class slack of the arena: what the entities hold, after a drain.
    private static long HeldBytes(EngineDatabase database)
    {
        var arena = database.Counters.Arena;

        return arena.LiveBytes + arena.ClassSlackBytes;
    }
}
