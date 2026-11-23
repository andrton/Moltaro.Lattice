using Moltaro.Lattice.Tests.Indexes.Model;

namespace Moltaro.Lattice.Tests.Indexes.Walk;

// The order and the bounds of a walk, held against the model of the order (IndexModel), in a UTF-8 and in a
// UTF-16 database (decision on index access; docs/decisions/queries.md, Q07.3.1 and Q07.3.6;
// docs/design/ordered-index.md, "Entries and their order" and "How a range is walked"):
// - over composite indexes of one to four parts, each of which can be null, so that a null stands in
//   every position of a key: a search by the leading parts of an index, every count of them, returns
//   the rows whose leading parts are its values, a null equal to a null, in the order of the index, with
//   the nulls of each part after its values; a range on the next part, inclusive or exclusive at each end
//   and either end open, returns those of them whose next part lies in it, and none whose next part is
//   null, since a null is in no range;
// - in reverse the answer is the exact mirror of the index order, so that a null comes first there
//   (docs/design/ordered-index.md, "How a range is walked"), and a reverse range without an upper end
//   starts at the last value before the nulls of its part and returns none of them;
// - the lazy Find and FindAsync give the same answer in the index order.
// The rows of one key come in no order the contract states, so an answer is compared as its keys in
// their order and its ids as a set. A composite key with a null before another part is among the keys
// these searches hold at their bounds.
public sealed class OrderAndBoundsTests
{
    public static IEnumerable<TheoryDataRow<string>> Encodings => WalkSchema.Encodings;

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Walk_by_leading_parts_and_a_range_returns_the_matches_of_the_oracle_in_the_index_order_and_in_its_exact_reverse(string encoding)
    {
        await using var database = await WalkSchema.OpenWithAsync(encoding, WalkRows.All);
        var rows = database.Set<Row>();

        foreach (var shape in WalkRows.Shapes())
        {
            var expected = shape.Answer(WalkRows.All);
            var index = rows.Index(shape.Index);
            var found = await index.FindAsync(shape.Key(), new LatticeFindOptions { Reverse = shape.Reverse });

            shape.Model.Expect(shape.ToString(), expected, [.. found.Items.Select(row => row.Entry())]);
            Assert.False(found.HasMore, $"{shape} has more, which only a search with a limit can have.");

            if (!shape.Reverse)
            {
                shape.Model.Expect(shape.ToString(), expected, [.. index.Find(shape.Key()).Select(row => row.Entry())]);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Range_on_a_nullable_part_without_an_upper_end_leaves_out_its_nulls_in_both_directions(string encoding)
    {
        // Under the leading value 7 of A, B is null in three rows and has a value in four; under 8, B is
        // null in every row; two rows have no A.
        Row[] stored =
        [
            WalkRows.Of(1, a: 7, b: "b"),
            WalkRows.Of(2, a: 7),
            WalkRows.Of(3, a: 7, b: "d"),
            WalkRows.Of(4, a: 7),
            WalkRows.Of(5, a: 7, b: "a"),
            WalkRows.Of(6, a: 7, b: "c"),
            WalkRows.Of(7, a: 7),
            WalkRows.Of(8, a: 8),
            WalkRows.Of(9, a: 8),
            WalkRows.Of(10),
            WalkRows.Of(11),
        ];

        await using var database = await WalkSchema.OpenWithAsync(encoding, stored);
        var byAB = database.Set<Row>().Index("ByAB");

        var forward = await byAB.FindAsync(IndexKey.Equal<int?>(7).Range(IndexBound.Inclusive("b"), null), new LatticeFindOptions());
        var reverse = await byAB.FindAsync(IndexKey.Equal<int?>(7).Range(IndexBound.Inclusive("b"), null), new LatticeFindOptions { Reverse = true });
        var reverseFromLowest = await byAB.FindAsync(IndexKey.Equal<int?>(7).Range(IndexBound.Exclusive(""), null), new LatticeFindOptions { Reverse = true });
        var onlyNulls = await byAB.FindAsync(IndexKey.Equal<int?>(8).Range(IndexBound.Inclusive(""), null), new LatticeFindOptions { Reverse = true });

        Assert.Equal([1L, 6L, 3L], forward.Items.Select(row => row.Id));
        Assert.Equal([3L, 6L, 1L], reverse.Items.Select(row => row.Id));
        Assert.Equal([3L, 6L, 1L, 5L], reverseFromLowest.Items.Select(row => row.Id));
        Assert.Empty(onlyNulls.Items);

        // The same through the index of one part, whose nulls end the index and are not in the range.
        var byA = database.Set<Row>().Index("ByA");
        var reverseByA = await byA.FindAsync(IndexKey.All.Range(IndexBound.Inclusive(7), null), new LatticeFindOptions { Reverse = true });

        Assert.Equal([8, 8, 7, 7, 7, 7, 7, 7, 7], reverseByA.Items.Select(row => row.A!.Value));
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L], reverseByA.Items.Select(row => row.Id).Order());
    }
}
