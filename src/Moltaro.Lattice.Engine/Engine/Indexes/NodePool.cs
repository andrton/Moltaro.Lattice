namespace Moltaro.Lattice.Engine.Indexes;

// The pool of the nodes reserved for the publications of one index, apart from its tree: the bound of what
// the changes prepared and not published can take from the tree, linear in their inserts and removes, kept
// by a ledger, and the reserved nodes linked through their own payload, so that a reservation takes no
// managed room for them (docs/design/ordered-index.md, "The reservation"). Not built yet: its two bounds
// are NotSupportedException.
internal sealed class NodePool
{
    private NodePool()
    {
    }

    // The most nodes the inserts and the removes given can take from a tree whose height was the one given
    // when nothing waited on it, over every order of their publications.
    public static long Bound(int height, int inserts, int removes) =>
        throw new NotSupportedException("The linear bound of the reservation is not implemented yet.");

    // The most places in the retired list the same publications can use.
    public static long PlacesBound(int height, int inserts, int removes) =>
        throw new NotSupportedException("The linear bound of the reservation is not implemented yet.");
}
