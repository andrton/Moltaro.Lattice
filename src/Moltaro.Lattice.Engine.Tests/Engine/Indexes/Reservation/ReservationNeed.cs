namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;

// The need of the writes that wait on an index, summed here one write at a time as the tree first
// defined it: the most nodes their publications can take, and the most places in the retired list they
// can use, from a tree of the height given whose root has the room given (docs/design/ordered-index.md,
// "The reservation"). The k-th insert counts 2 H + 1 at the height H it can find, the height now and the
// levels the k − 1 inserts before it can add; each remove counts H + 1 at the height after every insert.
// The inserts add a first level once they have used the room of the root, the inserts it takes before it
// can split, and a further level after every L more, L the separators an inner node holds before it is
// full. The tests hold the closed form, OrderedTree.Need, against this sum, and take the figures of the
// reservation from it, so that a figure does not come from the member it checks.
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

    // The levels the inserts given can add to the tree.
    private static long Growth(int inserts, int room, int leastSeparators) =>
        inserts <= room ? 0 : 1 + ((inserts - 1 - room) / leastSeparators);
}
