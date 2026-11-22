using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// A search by a full non-null key of a unique index returns at most one root, also when the value changes
// owner between two of its steps (docs/decisions/unique-indexes.md, "Non-null full-key equality has at most
// one matching root"; docs/design/ordered-index.md, "The search by a full key"). A lazy search takes a step
// for each entity; the step that returns the owner cannot see the end of the run when the owner is the last
// entry of its leaf and the fence of that leaf is a separator of the same key, the copy of an entry removed
// earlier (ValueAtTheEndOfALeaf). Between that step and the next, the owner moves away and another entity
// takes the value. The search is through the class and by name.
public sealed class LazySearchOfAValueTests
{
    [Fact]
    public async Task Lazy_search_by_a_full_key_of_a_unique_index_returns_at_most_one_root_while_the_value_changes_owner()
    {
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema);
        var numbered = database.Set<UniqueSchema.Numbered>();
        var value = await ValueAtTheEndOfALeaf.MakeAsync(database, "Numbered");

        var found = new List<UniqueSchema.Numbered>();

        using (var search = numbered.Index("ByNumber").Find(IndexKey.Equal(value)).GetEnumerator())
        {
            Assert.True(search.MoveNext());
            found.Add(search.Current);

            await ChangeOwnerAsync(numbered, value);

            while (search.MoveNext())
            {
                found.Add(search.Current);
            }
        }

        Assert.True(found.Count <= 1, $"The search by the full key {value} of ByNumber returned {found.Count} roots: {string.Join(", ", found.Select(entity => $"{entity.Id} with {entity.Number}"))}.");
    }

    [Fact]
    public async Task Lazy_search_by_name_by_a_full_key_of_a_unique_index_returns_at_most_one_root_while_the_value_changes_owner()
    {
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema);
        var numbered = database.Set<UniqueSchema.Numbered>();
        var value = await ValueAtTheEndOfALeaf.MakeAsync(database, "Numbered");

        var found = new List<LatticeRecord>();

        using (var search = database.Set("Numbered").Index("ByNumber").Find(IndexKey.Equal(value)).GetEnumerator())
        {
            Assert.True(search.MoveNext());
            found.Add(search.Current);

            await ChangeOwnerAsync(numbered, value);

            while (search.MoveNext())
            {
                found.Add(search.Current);
            }
        }

        Assert.True(found.Count <= 1, $"The search by name by the full key {value} of ByNumber returned {found.Count} roots: {string.Join(", ", found.Select(entity => $"{entity.Get<long>("Id")} with {entity.Get<long>("Number")}"))}.");
    }

    // Between two steps of the search the value changes owner: entity 1 moves away, and a new entity takes it.
    private static async Task ChangeOwnerAsync(ILatticeSet<UniqueSchema.Numbered> numbered, long value)
    {
        await numbered.ReplaceAsync(new UniqueSchema.Numbered { Id = 1, Number = 10 });
        await numbered.InsertAsync(new UniqueSchema.Numbered { Id = 1_000_000, Number = value });
    }
}
