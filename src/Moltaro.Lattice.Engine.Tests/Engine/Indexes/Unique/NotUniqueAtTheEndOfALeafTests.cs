using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// Only a search by the full key of a unique index ends at the entity a step returns; a search by the full
// key of an index that is not unique goes on past it, as any search does (docs/design/ordered-index.md, "The
// search by a full key"). Item, with ByNumber not unique, in the layout of ValueAtTheEndOfALeaf, where the
// step that returns entity 1 cannot see the end of the run, and a second entity with the same value: the lazy
// search and FindAsync return both, and FindAsync with a Limit of one says that a match follows.
public sealed class NotUniqueAtTheEndOfALeafTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Number", "Type": "Int64" } ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByNumber", "Fields": [2] } ]
            }
          ]
        }
        """);

    [Fact]
    public async Task Search_by_a_full_key_of_an_index_that_is_not_unique_returns_every_holder_across_the_end_of_a_leaf()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var set = database.Set("Item");
        var value = await ValueAtTheEndOfALeaf.MakeAsync(database, "Item");

        await set.InsertAsync(ValueAtTheEndOfALeaf.Of(set, 1_000_000, value));

        var byNumber = set.Index("ByNumber");
        var lazy = byNumber.Find(IndexKey.Equal(value)).Select(found => found.Get<long>("Id")).Order().ToList();
        var all = await byNumber.FindAsync(IndexKey.Equal(value), new LatticeFindOptions());
        var one = await byNumber.FindAsync(IndexKey.Equal(value), new LatticeFindOptions { Limit = 1 });

        Assert.Equal([1L, 1_000_000L], lazy);
        Assert.Equal([1L, 1_000_000L], all.Items.Select(found => found.Get<long>("Id")).Order());
        Assert.Single(one.Items);
        Assert.True(one.HasMore);
    }
}
