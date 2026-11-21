using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Search;

// FindAsync returns a row at most once, also when the row's key moves ahead of its walk after the walk
// returned it or passed over it by Offset, and when the row is deleted and created again under its key
// (docs/decisions/queries.md, Q07.3.5: "The same key must not be counted once in the skipped prefix and
// returned again after it moves in the index"). FindAsync takes every step in one call, so the test holds
// it inside its steps at the roots it takes (HeldSearch) and writes meanwhile. Rows 1 to 10 of Counted
// (IndexSchema) have the Number of their id, and the walk is of the range of Number from 1 to 20 through
// ByNumber:
// - in reverse, a row returned whose key then moves ahead of the walk, below the rows not reached, is not
//   returned again;
// - after an Offset, a row passed over whose key then moves ahead of the walk is neither counted nor
//   returned again, so the page is the one of the rows that kept their keys;
// - after an Offset, a row passed over that is deleted and created again under its key is not counted
//   again: ten rows of one Number, a run ordered by the identities of their root entries, which inserts
//   take in the order of the slots of the table, so that the row created again lies after the others,
//   ahead of the walk (RunEndTests says the same of a search by the full key).
// The forms in the index order and through the lazy Find are stated through the public API
// (MovedDuringAWalkTests in Moltaro.Lattice.Tests).
public sealed class MovedDuringAWalkTests
{
    private static readonly IndexKey Range = IndexKey.All.Range(IndexBound.Inclusive(1L), IndexBound.Inclusive(20L));

    [Fact]
    public async Task Row_returned_by_a_walk_in_reverse_whose_key_then_moves_ahead_of_it_is_not_returned_again()
    {
        await using var database = await OpenWithTenAsync();
        var counted = database.Set<IndexSchema.Counted>();

        using var held = await StartAsync(database, new LatticeFindOptions { Reverse = true });

        // The walk took row 10, the first in reverse, and goes on to the next root.
        await held.NextAsync();

        await counted.ReplaceAsync(new IndexSchema.Counted { Id = 10, Number = 2 });

        var found = await held.FinishAsync();

        Assert.Equal([10L, 9L, 8L, 7L, 6L, 5L, 4L, 3L, 2L, 1L], found.Items.Select(row => row.Id));
    }

    [Fact]
    public async Task Row_passed_over_by_an_Offset_whose_key_then_moves_ahead_of_the_walk_is_not_counted_or_returned_again()
    {
        await using var database = await OpenWithTenAsync();
        var counted = database.Set<IndexSchema.Counted>();

        using var held = await StartAsync(database, new LatticeFindOptions { Offset = 2 });

        // The walk took row 1, which its Offset passes over, and goes on to the next root.
        await held.NextAsync();

        await counted.ReplaceAsync(new IndexSchema.Counted { Id = 1, Number = 15 });

        var found = await held.FinishAsync();

        Assert.Equal([3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L], found.Items.Select(row => row.Id));
    }

    [Fact]
    public async Task Row_passed_over_by_an_Offset_and_created_again_under_its_key_is_not_counted_again()
    {
        await using var database = await OpenWithTenAsync(oneNumber: true);
        var counted = database.Set<IndexSchema.Counted>();

        using var held = await StartAsync(database, new LatticeFindOptions { Offset = 3, Limit = 4 });

        // The walk took row 1, which its Offset passes over, and goes on to the next root.
        await held.NextAsync();

        await counted.DeleteAsync(1L);
        await counted.InsertAsync(new IndexSchema.Counted { Id = 1, Number = 5 });

        var found = await held.FinishAsync();

        Assert.Equal([4L, 5L, 6L, 7L], found.Items.Select(row => row.Id));
        Assert.True(found.HasMore);
    }

    // Rows 1 to 10, of the Number of their id, or all of Number 5.
    private static async Task<EngineDatabase> OpenWithTenAsync(bool oneNumber = false)
    {
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= 10; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = oneNumber ? 5 : id });
        }

        return database;
    }

    // FindAsync of the range with the options, held at the first root it takes.
    private static Task<HeldSearch<LatticeFindResult<IndexSchema.Counted>>> StartAsync(EngineDatabase database, LatticeFindOptions options)
    {
        var byNumber = database.Set<IndexSchema.Counted>().Index("ByNumber");

        return HeldSearch<LatticeFindResult<IndexSchema.Counted>>.StartAsync(database, () => OwnThread.Wait(byNumber.FindAsync(Range, options)));
    }
}
