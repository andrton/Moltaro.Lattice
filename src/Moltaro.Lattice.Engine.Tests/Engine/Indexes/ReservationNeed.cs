using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// The need of the writes that wait on an index, summed here one write at a time as the tree first
// defined it: the most nodes their publications can take, and the most places in the retired list they
// can use, from a tree of the height given whose root has the room given (docs/design/ordered-index.md,
// "The reservation"). The k-th insert counts 2 H + 1 at the height H it can find, the height now and the
// levels the k − 1 inserts before it can add; each remove counts H + 1 at the height after every insert.
// The inserts add a first level once they have used the room of the root, the inserts it takes before it
// can split, and a further level after every L more, L the separators an inner node holds before it is
// full. The tests hold the closed form, NodePool.Need, against this sum, and take the figures of the
// reservation from it, so that a figure does not come from the member it checks.
//
// Beside it, the bound by levels of the same publications, counted here from the nodes of the tree by the
// rules of a split, without the members of the engine that count it (LevelBoundTests says why it holds).
internal static class ReservationNeed
{
    public static long Of(int height, int room, int leastSeparators, int inserts, int removes)
    {
        long need = 0;

        for (var before = 0; before < inserts; before++)
        {
            need += (2L * (height + Growth(before, room, leastSeparators))) + 1;
        }

        return need + (removes * (height + Growth(inserts, room, leastSeparators) + 1L));
    }

    // The standing reserve of an index at a tree: the need of one change of key, an insert and a remove.
    public static long OfOneChangeOfKey(int height, int room, int leastSeparators) => Of(height, room, leastSeparators, inserts: 1, removes: 1);

    // The linear bound of the nodes the inserts and removes given take from a tree of the height given, published
    // in any order: p (2 h + 3) + 2 q, which LinearBoundTests holds against what publications take and says why
    // it holds.
    public static long NodesBound(int height, int inserts, int removes) =>
        (inserts * ((2L * height) + 3)) + (2L * removes);

    // The bound by levels of the nodes the inserts and the removes given take from the tree whose nodes are
    // given, published in any order: p + 2 (s0 + s1 + ...) + 2 q, where sj is the most splits level j, from
    // the leaves up, can make (Splits).
    public static long NodesBound(IndexLayout layout, IReadOnlyList<(int Level, int Bytes)> nodes, long inserts, long removes) =>
        inserts + (2 * Splits(layout, nodes, inserts, removes).Sum()) + (2 * removes);

    // The bound by levels of the places in the retired list the same publications use: an insert retires
    // the nodes it splits and the one it copies, p + s0 + s1 + ...; a remove at most the nodes of its path
    // and the neighbour of a merge, at a height of at most one more than the levels that split.
    public static long PlacesBound(IndexLayout layout, IReadOnlyList<(int Level, int Bytes)> nodes, long inserts, long removes)
    {
        var splits = Splits(layout, nodes, inserts, removes);
        var height = nodes.Count == 0 ? 0 : nodes.Max(node => node.Level) + 1;
        var reach = inserts == 0 ? height : Math.Max(height, splits.Count + 1);

        return inserts + splits.Sum() + (removes * (reach + 1L));
    }

    // Every live node of the tree of the index, its level and the bytes it holds as a split counts them: the
    // entries of a leaf with their offsets, the separators of an inner node with their offsets and the child
    // after each.
    public static List<(int Level, int Bytes)> NodesOf(SecondaryIndex index)
    {
        var offset = index.Layout.HasText ? IndexLayout.OffsetBytes : 0;

        return [.. index.Tree.Nodes().Select(node => (node.Level, node.Items.Sum(item => item.Length + offset + (node.Level == 0 ? 0 : IndexLayout.ChildBytes))))];
    }

    // The levels the inserts given can add to the tree.
    private static long Growth(int inserts, int room, int leastSeparators) =>
        inserts <= room ? 0 : 1 + ((inserts - 1 - room) / leastSeparators);

    // The most splits of each level, from the leaves up, while a level can make one. A node can split only
    // when it may not fit the widest of its items; a node a split makes, a leaf of one entry and a new root
    // take some inserts before they can, the credit of their kind (Rule). Each node holds the part of its
    // credit its inserts have made, its progress; the progress of a level grows by at most one at an insert
    // that does not split, falls by the credit at a split, and grows by at most twice the credit at a remove
    // of a leaf: the last leaf counts its progress twice, since a full one that an entry after its end
    // leaves full may still split, and a remove may make another leaf the last one or merge two. So a level
    // that gets k inserts makes at most min(k, (progress + k) / (credit + 1)) splits, and sends as many
    // separators up.
    private static List<long> Splits(IndexLayout layout, IReadOnlyList<(int Level, int Bytes)> nodes, long inserts, long removes)
    {
        var leaf = Rule.ForLeaves(layout);
        var inner = Rule.ForInnerNodes(layout);
        var splits = new List<long>();
        var arriving = inserts;

        for (var level = 0; arriving > 0; level++)
        {
            var rule = level == 0 ? leaf : inner;
            var progress = nodes.Where(node => node.Level == level).Sum(node => (long)rule.ProgressOf(node.Bytes));

            if (level == 0)
            {
                progress += rule.Credit + (2L * rule.Credit * removes);
            }

            var made = Math.Min(arriving, (progress + arriving) / (rule.Credit + 1));

            if (made == 0)
            {
                break;
            }

            splits.Add(made);
            arriving = made;
        }

        return splits;
    }

    // What a node of one kind holds, and the inserts a node a publication makes takes before it can split: a
    // leaf holds its entries with their offsets; an inner node its separators with their offsets and the
    // child after each, the first child aside. A leaf that splits in the middle of its bytes leaves halves
    // of at most half of them, rounded up, and one widest entry less a byte, since the first half stops
    // before the entry that would pass the middle; an inner node leaves halves of at most half its bytes,
    // since the separator in the middle goes up. A new leaf holds one entry, and is counted for twice the
    // credit, since it may be the last leaf; a new root holds one separator.
    private readonly record struct Rule(int Capacity, int Widest, int Credit)
    {
        public static Rule ForLeaves(IndexLayout layout)
        {
            var capacity = layout.PayloadBytes - NodeHeader.Size;
            var widest = layout.WidestEntryBytes + (layout.HasText ? IndexLayout.OffsetBytes : 0);
            var half = ((capacity + widest + 1) / 2) + widest - 1;
            var rule = new Rule(capacity, widest, 0);

            return rule with { Credit = Math.Min(rule.InsertsBeforeFull(half), rule.InsertsBeforeFull(widest) / 2) };
        }

        public static Rule ForInnerNodes(IndexLayout layout)
        {
            var capacity = layout.PayloadBytes - NodeHeader.Size - IndexLayout.ChildBytes;
            var widest = layout.WidestEntryBytes + (layout.HasText ? IndexLayout.OffsetBytes : 0) + IndexLayout.ChildBytes;
            var half = (capacity + widest) / 2;
            var rule = new Rule(capacity, widest, 0);

            return rule with { Credit = Math.Min(rule.InsertsBeforeFull(half), rule.InsertsBeforeFull(widest)) };
        }

        // How many of the widest items a node of these bytes takes before it may not fit one more.
        public int InsertsBeforeFull(int bytes)
        {
            var inserts = 0;

            while (bytes + Widest <= Capacity)
            {
                bytes += Widest;
                inserts++;
            }

            return inserts;
        }

        public int ProgressOf(int bytes) => Credit - Math.Min(Credit, InsertsBeforeFull(bytes));
    }
}
