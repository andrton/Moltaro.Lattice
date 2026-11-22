using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// PROBE (breaker, S16). A search by a full non-null key of a unique index finds at most one root (the brief
// of S16, "A reader finds at most one root for a full non-null key of a unique index at every moment, under
// concurrent writes included"; decision on unique indexes, "Non-null full-key equality has at most one
// matching root"). A lazy search takes a step for each entity; the step that returns the owner cannot see
// the end of the run when the owner is the last entry of its leaf and the fence of that leaf is a separator
// of the same key, the copy of an entry removed earlier. Between that step and the next, the owner moves
// away and another entity takes the value.
public sealed class LazySearchOfAValueTests
{
    private const int Entities = 3000;

    [Fact]
    public async Task Lazy_search_by_a_full_key_of_a_unique_index_returns_at_most_one_root_while_the_value_changes_owner()
    {
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema);
        var numbered = database.Set<UniqueSchema.Numbered>();

        for (var id = 1L; id <= Entities; id++)
        {
            await numbered.InsertAsync(new UniqueSchema.Numbered { Id = id, Number = 10 * id });
        }

        Assert.True(database.Schema.TryGetEntityType("Numbered", out var type));

        var leaves = database.IndexNodes(type, "ByNumber").Where(node => node.Level == 0).Select(node => node.Items.Select(item => (long)item.Key[0]!).ToList()).ToList();
        var value = leaves[1][0];

        // The first entry of the second leaf goes, its separator stays; entity 1, whose root entry comes first,
        // takes the value, and its entry is the last of a leaf, below the separator of the same key.
        await numbered.DeleteAsync(value / 10);
        await numbered.ReplaceAsync(new UniqueSchema.Numbered { Id = 1, Number = value });

        var after = database.IndexNodes(type, "ByNumber").Where(node => node.Level == 0).Select(node => node.Items.Select(item => (long)item.Key[0]!).ToList()).ToList();

        Assert.Contains(after, leaf => leaf[^1] == value);
        Assert.DoesNotContain(after, leaf => leaf[0] == value);

        var found = new List<UniqueSchema.Numbered>();

        using (var search = numbered.Index("ByNumber").Find(IndexKey.Equal(value)).GetEnumerator())
        {
            Assert.True(search.MoveNext());
            found.Add(search.Current);

            // Between two steps of the search the value changes owner.
            await numbered.ReplaceAsync(new UniqueSchema.Numbered { Id = 1, Number = 10 });
            await numbered.InsertAsync(new UniqueSchema.Numbered { Id = 1_000_000, Number = value });

            while (search.MoveNext())
            {
                found.Add(search.Current);
            }
        }

        Assert.True(found.Count <= 1, $"The search by the full key {value} of ByNumber returned {found.Count} roots: {string.Join(", ", found.Select(entity => $"{entity.Id} with {entity.Number}"))}.");
    }
}
