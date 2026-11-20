using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.Indexes.IndexEntries;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Tree;

// An entry the tree of an index holds already is never added again, and one it does not hold is never
// taken away: either is refused before any store (OrderedTree.Insert, Remove; docs/design/ordered-index.md,
// "A child": an entry the stream of a child contradicts, one to insert that the index holds or one to
// remove that it does not, is a divergence, the publication fails and the child is faulted as on any
// failed publication). The test works the parts themselves — an arena with a root layout (RootArena), the
// retired list over the epochs of the protection of reads, and the index of Counted (SecondaryIndex) — as
// the writer does: it reserves, then inserts or removes. A refused insert or remove leaves the tree as it
// was: its entries, its live bytes and the bytes of the arena; and what was reserved for it is given back.
public sealed class ContradictionTests
{
    [Fact]
    public void Insert_of_an_entry_the_tree_holds_and_remove_of_one_it_does_not_hold_are_refused_before_any_store()
    {
        using var item = new RootArena(IndexSchema.Utf8, "Counted");
        var epochs = new ReadEpochs(new object());
        var retired = new RetiredMemory(epochs, item.Arena, new RootTable());
        Assert.True(item.EntityType.TryGetIndex("ByNumber", out var definition));
        var index = SecondaryIndex.Open(definition, item.Layout, item.Arena, retired);

        var held = EntryOf(item, index, new RootHandle(1, 1), number: 7);
        var absent = EntryOf(item, index, new RootHandle(1, 2), number: 7);

        Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
        index.Tree.Insert(held);
        retired.Drain();

        var count = index.Tree.Count;
        var bytes = index.Tree.Bytes;
        var arena = item.Arena.Counters;

        Assert.Equal(1, count);

        Assert.True(index.Tree.TryReserve(inserts: 1, removes: 0));
        Assert.Throws<InvalidOperationException>(() => index.Tree.Insert(held));
        index.Tree.Release(inserts: 1, removes: 0);

        Assert.True(index.Tree.TryReserve(inserts: 0, removes: 1));
        Assert.Throws<InvalidOperationException>(() => index.Tree.Remove(absent));
        index.Tree.Release(inserts: 0, removes: 1);

        retired.Drain();

        Assert.Equal((count, bytes), (index.Tree.Count, index.Tree.Bytes));
        Assert.Equal(arena, item.Arena.Counters);
    }
}
