namespace Moltaro.Lattice.Tests.Indexes.Walk;

// A walk returns a row at most once, also when the row's key moves in the index while the walk runs, or
// the row is deleted and created again (docs/decisions/queries.md, Q07.3.5: "The same key must not be
// counted once in the skipped prefix and returned again after it moves in the index", and "A delete
// followed by insertion of the same key does not permit a second result"). A walk remembers the rows it
// has returned or passed over (docs/design/ordered-index.md, "How a range will be walked"). The lazy Find
// is paused between two of its steps, and the test writes meanwhile:
// - a row returned, whose key then moves ahead of the walk, inside its range, is not returned again;
// - a row not reached yet whose key moves behind the walk is not returned, since it would come out of
//   order, and a row not reached yet whose key moves ahead of it is returned at most once; every row that
//   keeps its key is returned, none twice;
// - a row returned, then deleted and inserted again under the same key, is returned once: the range form
//   of RecreatedDuringASearchTests.
// The forms in reverse and in the skipped prefix of an Offset, which FindAsync takes in one call, are
// stated by the tests of the engine, which hold a step of it inside the engine (MovedDuringAWalkTests in
// Moltaro.Lattice.Engine.Tests).
public sealed class MovedDuringAWalkTests
{
    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Row_returned_by_a_walk_whose_key_then_moves_ahead_of_it_is_not_returned_again()
    {
        await using var database = await OpenWithTenAsync();
        var rows = database.Set<Row>();

        using var walk = rows.Index("ByA").Find(IndexKey.All.Range(IndexBound.Inclusive(1), IndexBound.Inclusive(20))).GetEnumerator();

        Assert.True(walk.MoveNext());
        Assert.Equal(1L, walk.Current.Id);

        // Ahead of the walk, still inside its range.
        await rows.ReplaceAsync(WalkRows.Of(1, a: 15));

        var rest = RestOf(walk);

        Assert.Equal([2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L], rest);
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Rows_not_reached_whose_keys_move_behind_or_ahead_of_a_walk_come_at_most_once_and_in_order_and_every_row_that_kept_its_key_is_returned()
    {
        await using var database = await OpenWithTenAsync();
        var rows = database.Set<Row>();

        using var walk = rows.Index("ByA").Find(IndexKey.All.Range(IndexBound.Inclusive(1), IndexBound.Inclusive(20))).GetEnumerator();

        Assert.True(walk.MoveNext());
        Assert.True(walk.MoveNext());
        Assert.Equal(2L, walk.Current.Id);

        // Row 8 behind the walk, row 4 ahead of it, both inside the range.
        await rows.ReplaceAsync(WalkRows.Of(8, a: 1));
        await rows.ReplaceAsync(WalkRows.Of(4, a: 12));

        var rest = RestOf(walk);

        Assert.Equal(rest.Count, rest.Distinct().Count());
        Assert.DoesNotContain(8L, rest);
        Assert.Equal([3L, 5L, 6L, 7L, 9L, 10L], rest.Where(id => id != 4));
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Walk_of_a_range_across_a_delete_and_a_re_creation_returns_each_row_of_the_range_exactly_once()
    {
        await using var database = await OpenWithTenAsync();
        var rows = database.Set<Row>();

        foreach (var byName in new[] { false, true })
        {
            IEnumerable<long> walked = byName
                ? database.Set("Row").Index("ByA").Find(IndexKey.All.Range(IndexBound.Inclusive(3), IndexBound.Exclusive(7))).Select(record => record.Get<long>("Id"))
                : rows.Index("ByA").Find(IndexKey.All.Range(IndexBound.Inclusive(3), IndexBound.Exclusive(7))).Select(row => row.Id);

            using var walk = walked.GetEnumerator();

            Assert.True(walk.MoveNext());
            var first = walk.Current;

            Assert.Equal(3L, first);

            await rows.DeleteAsync(first);
            await rows.InsertAsync(WalkRows.Of(first, a: 3, d: "again"));

            var found = new List<long> { first };

            while (walk.MoveNext())
            {
                found.Add(walk.Current);
            }

            Assert.Equal([3L, 4L, 5L, 6L], found);
        }
    }

    // A master whose rows 1 to 10 have A equal to their id.
    private static Task<ILatticeDatabase> OpenWithTenAsync() =>
        WalkSchema.OpenWithAsync("Utf8", Enumerable.Range(1, 10).Select(id => WalkRows.Of(id, a: id)));

    private static List<long> RestOf(IEnumerator<Row> walk)
    {
        var rest = new List<long>();

        while (walk.MoveNext())
        {
            rest.Add(walk.Current.Id);
        }

        return rest;
    }
}
