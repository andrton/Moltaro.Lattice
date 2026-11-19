namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Where a node of an index fills and splits (docs/design/ordered-index.md, "Nodes in the arena", "Writes"):
// a leaf of 1 KiB holds 61 entries of an Int64 key, 16 bytes each; the next entry splits it. One appended
// after the last entry of the last leaf leaves the full leaf as it is and starts a new one, so ascending
// keys leave full leaves; one that falls inside splits the leaf in the middle of its bytes. A root that
// splits gets a new root above it, whose separator lies between the two leaves: every entry of the left
// one below it, every entry of the right one at or above it. A leaf holds at least four of the widest
// entries of its index: four entries of three text parts of 64 bytes, copies all, fill a leaf of 1 KiB, and
// the fifth splits it, while texts of 65 bytes, shared, take less room and five fit.
public sealed class NodeBoundaryTests
{
    private const int FullLeaf = 61;

    [Fact(Skip = SkipReason.BeforeSprint14)]
    public async Task Leaf_holds_61_Int64_entries_and_the_62nd_appended_after_them_starts_a_new_leaf_under_a_new_root()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= FullLeaf; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id * 10 });
        }

        var full = IndexContents.Of(database, "Counted", "ByNumber");

        Assert.Equal((1, 1), (full.Nodes, full.Height));
        Assert.Equal([FullLeaf], full.LeafCounts);

        await counted.InsertAsync(new IndexSchema.Counted { Id = FullLeaf + 1, Number = (FullLeaf + 1) * 10 });

        var split = IndexContents.Of(database, "Counted", "ByNumber");

        Assert.Equal((3, 2), (split.Nodes, split.Height));
        Assert.Equal([FullLeaf, 1], split.LeafCounts);
        ExpectSeparated(split);
    }

    [Fact(Skip = SkipReason.BeforeSprint14)]
    public async Task Entry_that_falls_inside_a_full_leaf_splits_it_in_the_middle()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();

        for (var id = 1L; id <= FullLeaf; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id * 10 });
        }

        await counted.InsertAsync(new IndexSchema.Counted { Id = FullLeaf + 1, Number = 305 });

        var split = IndexContents.Of(database, "Counted", "ByNumber");

        Assert.Equal((3, 2), (split.Nodes, split.Height));
        Assert.Equal(FullLeaf + 1, split.LeafCounts.Sum());
        Assert.All(split.LeafCounts, count => Assert.InRange(count, (FullLeaf + 1) / 2 - 1, (FullLeaf + 1) / 2 + 1));
        ExpectSeparated(split);
    }

    [Fact(Skip = SkipReason.BeforeSprint14)]
    public async Task Leaf_holds_four_of_the_widest_entries_of_three_copied_texts_and_five_of_three_shared_ones()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var tripled = database.Set<IndexSchema.Tripled>();

        for (var id = 1L; id <= 4; id++)
        {
            await tripled.InsertAsync(TripledOf(id, 64));
        }

        Assert.Equal([4], IndexContents.Of(database, "Tripled", "ByAll").LeafCounts);

        await tripled.InsertAsync(TripledOf(5, 64));

        var split = IndexContents.Of(database, "Tripled", "ByAll");

        Assert.Equal(2, split.Height);
        Assert.Equal(5, split.LeafCounts.Sum());

        await using var sharing = await EngineMaster.OpenAsync(IndexSchema.Utf8);

        for (var id = 1L; id <= 5; id++)
        {
            await sharing.Set<IndexSchema.Tripled>().InsertAsync(TripledOf(id, 65));
        }

        Assert.Equal([5], IndexContents.Of(sharing, "Tripled", "ByAll").LeafCounts);
    }

    // Every entry of the left leaf below the separator of the root, every entry of the right one at or above
    // it; the keys here are distinct, so the key tells.
    private static void ExpectSeparated(IndexContents tree)
    {
        var separator = (long)Assert.Single(Assert.Single(tree.RootSeparators))!;
        var entries = tree.Entries;
        var left = tree.LeafCounts[0];

        Assert.All(entries.Take(left), entry => Assert.True((long)entry.Key[0]! < separator));
        Assert.All(entries.Skip(left), entry => Assert.True((long)entry.Key[0]! >= separator));
    }

    // An entity whose three texts have the length given, in ASCII, and differ from those of every other.
    private static IndexSchema.Tripled TripledOf(long id, int length) => new()
    {
        Id = id,
        First = $"{id}a".PadRight(length, 'a'),
        Second = $"{id}b".PadRight(length, 'b'),
        Third = $"{id}c".PadRight(length, 'c'),
    };
}
