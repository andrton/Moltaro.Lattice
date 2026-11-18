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

    // The replaces a held read keeps from being freed, each of a Name as long as one segment: 16 MiB with
    // the segment of 4 MiB the database opens with.
    private const int HeldReplaces = 4;

    // The deletes of an absent key, which retire nothing, that a test is ready to make.
    private const int AbsentDeletes = 2_000;

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

    // What waits between two looks is bounded by the two thresholds (docs/design/arena-protocol.md,
    // "Deferred reclamation, 16 November 2026"), beside what reads hold. What a look leaves behind, because
    // a read still holds it, stays retired and is freed by a later look, and the count toward the next look
    // starts at the look. Such a look arms a second trigger: until a look leaves nothing behind, a published
    // write that retires nothing counts as one item. So the next look comes one window of writes of any kind
    // after the last: as many as the epoch table has slots, or fewer when the bytes retired reach one
    // segment first. A read is held (HeldRead) while four replaces of a piece with a Name of one segment's
    // length retire 16 MiB: each retires a segment of bytes or more, so each ends in a look, which frees
    // nothing and leaves everything behind. Once the read has left, deletes of a key that is absent, which
    // retire nothing, count one item each: a window holds more than one item, so the first delete frees
    // nothing, and the look within the window, as many deletes as the epoch table has slots, frees
    // everything, of the 2,000 the test is ready to make.
    [Fact]
    public async Task Memory_a_held_read_kept_from_looks_is_freed_within_one_window_of_writes_after_it_left_also_when_they_retire_nothing()
    {
        var slots = new ReadEpochs(new object()).SlotCount;

        Assert.True(slots > 1, $"The epoch table has {slots} slot, so a window of writes is one write.");

        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(PieceSchema.Of(1, name: Segment));
        await pieces.InsertAsync(PieceSchema.Of(2, name: 8));
        database.DrainRetired();

        using var held = await HeldRead.StartAsync(database, () => pieces.Get(2L)?.Count);

        for (var write = 1; write <= HeldReplaces; write++)
        {
            await pieces.ReplaceAsync(PieceSchema.Of(1, name: Segment));
        }

        Assert.True(database.Counters.Arena.RetiredBytes >= (long)HeldReplaces * Segment, "The looks the replaces made freed what a held read could reach.");
        await held.ReleaseAsync();

        Assert.False((await pieces.DeleteAsync(404L)).Changed);
        Assert.True(database.Counters.Arena.RetiredBytes > 0, "The first delete after the read left made a look: a write looked before its window was full.");

        var deletes = 1;

        while (database.Counters.Arena.RetiredBytes > 0 && deletes < AbsentDeletes)
        {
            Assert.False((await pieces.DeleteAsync(404L)).Changed);
            deletes++;
        }

        Assert.True(
            database.Counters.Arena.RetiredBytes == 0 && deletes <= slots,
            $"After {deletes} deletes of an absent key the arena holds {database.Counters.Arena.RetiredBytes} retired bytes; one window of writes is {slots} of them.");
    }

    // Only a look that leaves retired items behind arms the count of writes that retire nothing; without
    // one, also while something retired waits below both thresholds, such a write counts nothing, so a load
    // without a held read looks as often as it would without that count. With no read held, one replace
    // retires one root, far below both thresholds, and no look has left anything behind: 2,000 deletes of a
    // key that is absent bring no look, and the root stays retired through them; then replaces up to one
    // window of retirements, the first replace counted, bring the look that frees everything.
    [Fact(Skip = SkipReason.BeforeTheFixesAfterSprint13)]
    public async Task Writes_that_retire_nothing_count_nothing_while_no_look_has_left_retired_memory_behind()
    {
        var slots = new ReadEpochs(new object()).SlotCount;

        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, Numbers));
        database.DrainRetired();
        var root = HeldBytes(database);
        var window = (int)Math.Min(slots, (Segment + root - 1) / root);

        Assert.True(window > 1, $"One retired root of {root} bytes fills a window of retirements.");

        await stamps.ReplaceAsync(StampSchema.StampOf(1, Numbers + 1));

        for (var write = 0; write < AbsentDeletes; write++)
        {
            Assert.False((await stamps.DeleteAsync(404L)).Changed);
        }

        Assert.True(
            database.Counters.Arena.RetiredBytes == root,
            $"After one replace and {AbsentDeletes} deletes of an absent key the arena holds {database.Counters.Arena.RetiredBytes} retired bytes, not the {root} of the replaced root: the deletes brought a look although no look had left anything behind.");

        for (var write = 2; write <= window; write++)
        {
            await stamps.ReplaceAsync(StampSchema.StampOf(1, Numbers + write));
        }

        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    // With nothing retired waiting, a write that retires nothing counts nothing toward the next look, so a
    // run of such writes does not bring it forward: 2,000 deletes of a key that is absent, and then replaces
    // of one root, free nothing until the replaces have filled one window of retirements — as many as the
    // epoch table has slots, or fewer when their bytes reach one segment first — and the look that window
    // brings, with no read in progress, frees everything. No member shows a look itself; a look with no read
    // in progress shows by what it frees.
    [Fact]
    public async Task Writes_that_retire_nothing_while_nothing_retired_waits_do_not_bring_the_next_look_forward()
    {
        var slots = new ReadEpochs(new object()).SlotCount;

        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, Numbers));
        database.DrainRetired();
        var root = HeldBytes(database);

        for (var write = 0; write < AbsentDeletes; write++)
        {
            Assert.False((await stamps.DeleteAsync(404L)).Changed);
        }

        var window = (int)Math.Min(slots, (Segment + root - 1) / root);

        for (var write = 1; write < window; write++)
        {
            await stamps.ReplaceAsync(StampSchema.StampOf(1, Numbers + write));

            Assert.True(
                database.Counters.Arena.RetiredBytes == write * root,
                $"After {AbsentDeletes} deletes of an absent key and {write} replaces the arena holds {database.Counters.Arena.RetiredBytes} retired bytes, not the {write * root} the replaces retired: a look came before a window of {window} retirements.");
        }

        await stamps.ReplaceAsync(StampSchema.StampOf(1, Numbers + window));

        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    // The live bytes and the class slack of the arena: what the entities hold, after a drain.
    private static long HeldBytes(EngineDatabase database)
    {
        var arena = database.Counters.Arena;

        return arena.LiveBytes + arena.ClassSlackBytes;
    }
}
