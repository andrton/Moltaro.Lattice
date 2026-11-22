using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Unique;

// The owner of a value of a unique index is found by one descent of the published tree to the first
// entry of the value, and one more descent at the fence when that place is the end of a leaf, as a walk
// goes on to the leaf beside (docs/design/ordered-index.md), so the value is found taken wherever its
// entry lies: as the last entry of a
// leaf, as the first entry of the next, and equal to a separator. A separator is a copy of an entry that
// stays in its inner node when the entry leaves its leaf, so it may be the copy of a removed entry: the
// value of a removed owner is free while the separator stays, and once it is taken again it is taken.
// Numbered of UniqueSchema, inserted in ascending order, Number ten times the Id, so that its entries
// fill full leaves and the tree has three levels; the tests read the tree through the test member of the
// engine (EngineDatabase.IndexNodes) and choose the values at its boundaries from it.
public sealed class OwnerAtLeafBoundaryTests
{
    private const int Entities = 3000;

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Value_held_as_the_last_entry_of_a_leaf_as_the_first_of_the_next_and_equal_to_a_separator_is_taken()
    {
        await using var database = await OpenAsync();
        var numbered = database.Set<UniqueSchema.Numbered>();
        var tree = Tree.Of(database);

        Assert.True(tree.Height >= 3, $"The premise: a tree of three levels or more; it has {tree.Height}.");

        var next = 100_000L;

        for (var leaf = 0; leaf + 1 < tree.Leaves.Count; leaf++)
        {
            await ExpectTakenAsync(numbered, next++, tree.Leaves[leaf][^1]);
            await ExpectTakenAsync(numbered, next++, tree.Leaves[leaf + 1][0]);
        }

        foreach (var separator in tree.Separators)
        {
            await ExpectTakenAsync(numbered, next++, separator);
        }

        // A refused write changes no node, so every boundary above was the boundary of the tree as it is.
        Assert.Equal(tree.Leaves, Tree.Of(database).Leaves);
        Assert.Equal(tree.Separators, Tree.Of(database).Separators);

        // A value between the last entry of a leaf and the first of the next is free.
        var between = tree.Leaves[0][^1] + 5;

        Assert.Equal(tree.Leaves[0][^1] + 10, tree.Leaves[1][0]);
        await numbered.InsertAsync(new UniqueSchema.Numbered { Id = next, Number = between });
        Assert.Equal(Entities + 1, numbered.Enumerate().Count());
    }

    [Fact(Skip = SkipReason.UniqueIndexes)]
    public async Task Value_of_an_owner_removed_at_a_boundary_while_the_tree_keeps_its_separator_is_free_and_then_taken_once()
    {
        await using var database = await OpenAsync();
        var numbered = database.Set<UniqueSchema.Numbered>();
        var tree = Tree.Of(database);
        var separator = tree.Separators[0];
        var last = tree.Leaves[0][^1];

        // The first entry of the second leaf, and the last of the first, go; the leaves are full, so neither
        // merges.
        await numbered.DeleteAsync(separator / 10);
        await numbered.DeleteAsync(last / 10);

        var after = Tree.Of(database);

        Assert.Contains(separator, after.Separators);
        Assert.Equal(tree.Leaves.Count, after.Leaves.Count);
        Assert.DoesNotContain(separator, after.Leaves.SelectMany(leaf => leaf));
        Assert.DoesNotContain(last, after.Leaves.SelectMany(leaf => leaf));

        foreach (var value in new[] { separator, last })
        {
            await numbered.InsertAsync(new UniqueSchema.Numbered { Id = value + 1_000_000, Number = value });
            await ExpectTakenAsync(numbered, value + 2_000_000, value);

            var owners = numbered.Index("ByNumber").Find(IndexKey.Equal(value)).Select(found => found.Id).ToList();

            Assert.Equal([value + 1_000_000], owners);
        }
    }

    private static async Task<EngineDatabase> OpenAsync()
    {
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema);
        var numbered = database.Set<UniqueSchema.Numbered>();

        for (var id = 1L; id <= Entities; id++)
        {
            await numbered.InsertAsync(new UniqueSchema.Numbered { Id = id, Number = 10 * id });
        }

        return database;
    }

    // An insert of another entity with the number given, and a replace of entity 1 onto it, are refused
    // naming ByNumber, and the entity of the id is not stored.
    private static async Task ExpectTakenAsync(ILatticeSet<UniqueSchema.Numbered> numbered, long id, long number)
    {
        await UniqueSchema.ExpectTakenAsync("ByNumber", numbered.InsertAsync(new UniqueSchema.Numbered { Id = id, Number = number }).AsTask());

        if (number != 10)
        {
            await UniqueSchema.ExpectTakenAsync("ByNumber", numbered.ReplaceAsync(new UniqueSchema.Numbered { Id = 1, Number = number }).AsTask());
        }

        Assert.Null(numbered.Get(id));
    }

    // The tree of ByNumber as the engine holds it: the numbers of each leaf in the order of the index, the
    // numbers of the separators of every inner node, and its height.
    private sealed record Tree(List<List<long>> Leaves, List<long> Separators, int Height)
    {
        public static Tree Of(EngineDatabase database)
        {
            Assert.True(database.Schema.TryGetEntityType("Numbered", out var type));

            var nodes = database.IndexNodes(type, "ByNumber");
            var leaves = nodes.Where(node => node.Level == 0).Select(node => node.Items.Select(item => (long)item.Key[0]!).ToList()).ToList();
            var separators = nodes.Where(node => node.Level > 0).SelectMany(node => node.Items.Select(item => (long)item.Key[0]!)).ToList();

            return new Tree(leaves, separators, nodes.Max(node => node.Level) + 1);
        }
    }
}
