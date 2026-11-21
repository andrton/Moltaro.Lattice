namespace Moltaro.Lattice.Tests.Indexes.Walk;

// Offset and Limit of FindAsync (LatticeFindOptions; docs/decisions/queries.md, Q07.3.4): the matches are
// found in the order asked for, Offset of them are skipped and at most Limit are returned, and HasMore
// says whether a match follows those returned.
// - Against the oracle of the order, in both directions, for an Offset and a Limit at the start, inside the
//   matches and beyond them. Entities with the same indexed values come in no order the contract states
//   (docs/decisions/queries.md, Q07.3.3; LatticeFindResult.Items), so the rows of a run of equal keys that
//   Offset or Limit cuts cannot be named: a page has the keys of the oracle in their order, no row twice,
//   and only rows that match with the key of their place; a page that cuts no run has the rows of the
//   oracle (WalkOracle.ExpectPage).
// - Offset skips without returning, and Limit bounds the walk without reading every match first. Stated
//   by what a search holds, not by time: the memory limit of a read counts what a walk remembers, the keys
//   of the rows it returned or passed over and, until FindAsync returns, its result
//   (LatticeDatabaseOptions.ReadMemoryLimit). Under a limit that admits a page of 20 rows after an Offset
//   of 20, but not the 20,000 rows that match, the page succeeds and the search of every match fails with
//   ReadMemory.
public sealed class OffsetAndLimitTests
{
    private const int Matches = 20_000;

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Offset_and_Limit_return_the_part_of_the_matches_the_oracle_gives_with_HasMore_in_both_directions()
    {
        await using var database = await WalkSchema.OpenWithAsync("Utf16", WalkRows.All);
        var byAB = database.Set<Row>().Index("ByAB");

        foreach (var reverse in new[] { false, true })
        {
            foreach (var shape in new[] { new WalkShape(2, 0, null, null, null, reverse), new WalkShape(2, 1, WalkRows.All[0], null, null, reverse) })
            {
                var all = WalkOracle.Walk(WalkRows.All, shape);

                foreach (var (offset, limit) in new (int, int?)[] { (0, 1), (0, 10), (5, 10), (all.Count - 3, 10), (all.Count - 3, 3), (all.Count, 1), (all.Count + 5, null), (3, null) })
                {
                    var found = await byAB.FindAsync(shape.Key(), new LatticeFindOptions { Reverse = reverse, Offset = offset, Limit = limit });

                    WalkOracle.ExpectPage(shape, all, offset, limit, found.Items);
                    Assert.Equal(limit is { } most && offset + most < all.Count, found.HasMore);
                }
            }
        }
    }

    [Fact(Skip = SkipReason.BeforeSprint15)]
    public async Task Page_after_an_Offset_succeeds_under_a_read_memory_limit_that_the_search_of_every_match_exceeds()
    {
        await using var database = await WalkSchema.OpenAsync("Utf8", readMemoryLimit: 64 * 1024);
        var rows = database.Set<Row>();

        for (var id = 1L; id <= Matches; id++)
        {
            await rows.InsertAsync(WalkRows.Of(id, a: 1, c: id));
        }

        var byABC = rows.Index("ByABC");

        foreach (var reverse in new[] { false, true })
        {
            var page = await byABC.FindAsync(IndexKey.Equal<int?>(1), new LatticeFindOptions { Reverse = reverse, Offset = 20, Limit = 20 });

            Assert.Equal(20, page.Items.Count);
            Assert.True(page.HasMore);
            Assert.Equal(reverse ? Matches - 20 : 21, page.Items[0].C);

            var every = await Assert.ThrowsAsync<LatticeException>(async () => await byABC.FindAsync(IndexKey.Equal<int?>(1), new LatticeFindOptions { Reverse = reverse }));

            Assert.Equal(LatticeErrorEnum.ReadLimitExceeded, every.Error);
            Assert.Equal(LatticeLimitEnum.ReadMemory, every.Limit);
        }

        // A first page of the same search, which reads no match past its Limit.
        var first = await byABC.FindAsync(IndexKey.Equal<int?>(1), new LatticeFindOptions { Limit = 20 });

        Assert.Equal(Enumerable.Range(1, 20).Select(id => (long?)id), first.Items.Select(row => row.C));
    }
}
