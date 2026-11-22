using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// The layout in which the step of a search by a full key that returns an entity cannot see the end of the
// run (docs/design/ordered-index.md, "The search by a full key"): the entry of that entity is the last of its
// leaf, below a separator of the same key, the copy of an entry removed earlier. Made in an entity type with
// Id and Number, Int64 both, and the index ByNumber (Number), unique or not, by name: 3000 entities with
// Number 10 times Id, whose entries fill many leaves of nodes of 1 KiB; the first entry of the second leaf
// goes, its separator stays, and entity 1, whose root entry comes first, takes its value.
internal static class ValueAtTheEndOfALeaf
{
    private const int Entities = 3000;

    // Makes the layout and returns the value entity 1 holds.
    public static async Task<long> MakeAsync(EngineDatabase database, string entityType)
    {
        var set = database.Set(entityType);

        for (var id = 1L; id <= Entities; id++)
        {
            await set.InsertAsync(Of(set, id, 10 * id));
        }

        var value = IndexTree<long>.Of(database, entityType, "ByNumber").Leaves[1][0];

        await set.DeleteAsync(value / 10);
        await set.ReplaceAsync(Of(set, 1, value));

        var after = IndexTree<long>.Of(database, entityType, "ByNumber").Leaves;

        Assert.Contains(after, leaf => leaf[^1] == value);
        Assert.DoesNotContain(after, leaf => leaf[0] == value);

        return value;
    }

    // A record of the entity type of the set with its Id and Number.
    public static LatticeRecord Of(ILatticeSet set, long id, long number) => new(set.EntityType) { ["Id"] = id, ["Number"] = number };
}
