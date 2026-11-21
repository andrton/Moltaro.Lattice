using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// A walk of a range is a step at a time and holds nothing of the database between two steps: no node, no
// address and no slot of the protection (docs/design/ordered-index.md, "How a range will be walked"). The
// walk is of the range of Number from 100 to 200 through ByNumber of Counted (IndexSchema), over rows that
// keep their key, among them a run of 60 rows of one key, longer than a leaf, and rows that move.
// - A walk paused after its first row, its enumerator kept, holds back nothing that writes retire: while
//   it is paused, writes move rows out of the range, back into it and ahead of the walk, and insert and
//   delete rows inside the run, which replaces nodes of the index and roots, and a drain frees all of it.
//   Resumed, it returns every row that kept its key, none twice, only rows of the range, in the order of
//   the index.
// - A walk resumed after the tree grew by levels between two of its steps returns the same.
// - A walk abandoned between two steps, after it has returned more than one row, is held by nothing of the
//   database: once the test lets go of its enumerator, the enumerator is collected.
// - A walk in reverse, held inside its steps at the roots it takes (HeldSearch) while rows move, returns
//   every row that kept its key, none twice, in the reverse of the index order: the paused form of the
//   reverse, which FindAsync takes in one call.
public sealed class PausedWalkTests
{
    private const long Low = 100;

    private const long High = 200;

    private const int Run = 60;

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Walk_paused_between_two_steps_holds_back_nothing_that_writes_retire_and_resumes_with_every_row_that_kept_its_key()
    {
        await using var database = await OpenWithRowsAsync();
        var counted = database.Set<IndexSchema.Counted>();

        using var walk = counted.Index("ByNumber").Find(Range).GetEnumerator();

        Assert.True(walk.MoveNext());
        var found = new List<IndexSchema.Counted> { walk.Current };

        await MoveAsync(counted);

        for (var id = 1001L; id <= 1100; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 150 });
        }

        for (var id = 1001L; id <= 1100; id++)
        {
            await counted.DeleteAsync(id);
        }

        database.DrainRetired();

        Assert.Equal(0, database.Counters.Arena.RetiredBytes);

        while (walk.MoveNext())
        {
            found.Add(walk.Current);
        }

        ExpectKept(found, reverse: false);
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Walk_resumed_after_the_tree_grew_by_levels_between_two_of_its_steps_returns_every_row_that_kept_its_key()
    {
        await using var database = await OpenWithRowsAsync();
        var counted = database.Set<IndexSchema.Counted>();
        var heightBefore = HeightOf(database, counted);

        using var walk = counted.Index("ByNumber").Find(Range).GetEnumerator();

        Assert.True(walk.MoveNext());
        var found = new List<IndexSchema.Counted> { walk.Current };

        for (var id = 10_001L; id <= 30_000; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 5_000 + id });
        }

        Assert.True(HeightOf(database, counted) > heightBefore, "The tree did not grow by a level.");

        while (walk.MoveNext())
        {
            found.Add(walk.Current);
        }

        ExpectKept(found, reverse: false);
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Walk_abandoned_between_two_steps_after_more_than_one_row_is_held_by_nothing_of_the_database()
    {
        await using var database = await OpenWithRowsAsync();
        var counted = database.Set<IndexSchema.Counted>();

        var abandoned = Abandon(counted);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(abandoned.IsAlive, "The database holds a walk that was abandoned between two steps.");
        ExpectKept([.. counted.Index("ByNumber").Find(Range)], reverse: false);
        GC.KeepAlive(database);
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Walk_in_reverse_held_inside_its_steps_while_rows_move_returns_every_row_that_kept_its_key_none_twice()
    {
        await using var database = await OpenWithRowsAsync();
        var counted = database.Set<IndexSchema.Counted>();
        var byNumber = counted.Index("ByNumber");

        using var held = await HeldSearch<LatticeFindResult<IndexSchema.Counted>>.StartAsync(
            database,
            () => OwnThread.Wait(byNumber.FindAsync(Range, new LatticeFindOptions { Reverse = true })));

        await MoveAsync(counted);
        await held.NextAsync();
        await held.NextAsync();

        for (var id = 1001L; id <= 1100; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 150 });
        }

        await held.NextAsync();
        await MoveAsync(counted);

        var found = await held.FinishAsync();

        ExpectKept([.. found.Items.Where(row => row.Id < 1001)], reverse: true);
    }

    private static IndexKey Range => IndexKey.All.Range(IndexBound.Inclusive(Low), IndexBound.Inclusive(High));

    // Rows 1 to 60 of Number 150, the run; rows 61 to 80 from 105 to 200 by fives; the moving rows 101 to 150
    // at 130; rows 201 to 240 outside the range.
    private static async Task<EngineDatabase> OpenWithRowsAsync()
    {
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= Run; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 150 });
        }

        for (var id = 61L; id <= 80; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = Low + ((id - 60) * 5) });
        }

        for (var id = 101L; id <= 150; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 130 });
        }

        for (var id = 201L; id <= 240; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = 2_000 + id });
        }

        return database;
    }

    // The moving rows leave the range, a third come back to their key, a third move to the other end of
    // the range, and a third stay outside.
    private static async Task MoveAsync(ILatticeSet<IndexSchema.Counted> counted)
    {
        for (var id = 101L; id <= 150; id++)
        {
            await counted.ReplaceAsync(new IndexSchema.Counted { Id = id, Number = 9_000 + id });
        }

        for (var id = 101L; id <= 150; id++)
        {
            var number = (id % 3) switch
            {
                0 => 130,
                1 => id % 2 == 0 ? Low : High,
                _ => 9_000 + id,
            };

            await counted.ReplaceAsync(new IndexSchema.Counted { Id = id, Number = number });
        }
    }

    // Every row that kept its key, rows 1 to 80, is among the rows found, no row twice, every row found in
    // the range, and in the order of the index or its reverse.
    private static void ExpectKept(IReadOnlyList<IndexSchema.Counted> found, bool reverse)
    {
        var ids = found.Select(row => row.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(Enumerable.Range(1, 80), id => Assert.Contains((long)id, ids));
        Assert.All(found, row => Assert.InRange(row.Number, Low, High));

        for (var place = 1; place < found.Count; place++)
        {
            var order = found[place - 1].Number.CompareTo(found[place].Number);

            Assert.True(reverse ? order >= 0 : order <= 0, $"{found[place].Id} at {found[place].Number} came after {found[place - 1].Id} at {found[place - 1].Number}.");
        }
    }

    // The levels of the tree of ByNumber.
    private static int HeightOf(EngineDatabase database, ILatticeSet<IndexSchema.Counted> counted) =>
        database.IndexNodes(counted.EntityType, "ByNumber").Max(node => node.Level) + 1;

    // In a frame of its own, so that nothing of the test's frame references it: a walk that took two steps
    // and was let go of, without its enumerator being disposed.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Abandon(ILatticeSet<IndexSchema.Counted> counted)
    {
        var walk = counted.Index("ByNumber").Find(Range).GetEnumerator();

        Assert.True(walk.MoveNext());
        Assert.True(walk.MoveNext());

        return new WeakReference(walk);
    }
}
