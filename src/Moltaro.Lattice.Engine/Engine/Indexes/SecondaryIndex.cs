using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Indexes;

// One declared secondary index of one entity type on one node, kept with the data: its parts from the
// schema, the layout of its entries (IndexLayout), their order (IndexOrder) and its tree (OrderedTree)
// (decision on index access; docs/design/ordered-index.md). The table of an entity type holds one for
// each index its type declares. A single-field index has one part and a composite one several, in the
// order the schema declares; a part may be null, and a null is a value of the index like any other.
//
// The key of a root. Every state of a root contributes one entry to the index, made from its stored
// values (IndexLayout.WriteEntry), under the identity of its root entry. An entry is a hint: a search
// reads the root of each entry of its run and returns the entity only if the current state of the root
// has the key it looks for (HasKey). So a reader never finds an entity under a key the entity no longer
// has, and finds every entity that has the key for the whole search.
//
// A write. What a write does to the index is decided once, in the hold that prepares it, once its new
// root is written: the entry the root it replaces contributes against the entry the new root
// contributes, the same bytes being nothing (ChangeOf). Both roots stay as they are until the write is
// published or withdrawn (Writes.WriteCoordinator), and the decision is recorded with the prepared change
// (Entities.PreparedChange), which every later step reads; an entry is built only by the step that
// inserts, removes or rewrites it. What the place where the effects of a write are installed calls, for
// each index the write changes, under the lock of the writes, on a master and on a child alike:
// - in the hold that prepares the write: TryReserve; false when the arena cannot hold what the change
//   needs, which refuses the write as one the memory budget cannot hold, with nothing taken; then, once
//   every index has what the write needs, the top-up of the standing reserve (TopUp);
// - when the write is withdrawn before its publication: Release;
// - in its publication, before the stores that publish the root and the position: InstallNew;
// - after the position, before the old root or the deleted entry is retired: RemoveOld.
// So the entries of one publication are installed in this order: the new entries, then the root, then
// the old entries go; each by its own stores, and the check of a found entry against its root covers
// the moments between. A child that applies a transaction of several effects installs the new entries
// of every effect before its position and removes the old ones after it.
//
// A unique index lets a value, a key with no null part, belong to one root only (decision on unique
// indexes): a write that sets or moves a value is checked against the published tree before anything is
// reserved for it, by one descent to the first entry of the value (TryFindOwner); the prepared and
// unpublished writes that claim a value are the write coordinator's to know (Entities.ChangePublisher,
// Writes.WriteCoordinator). A key with a null in any part is not compared.
//
// A child builds its indexes while it loads, an entry for each root it places, through the same members,
// before it is ready, and keeps them while it follows. An entry its stream contradicts, one to add that
// the index holds or one to take away that it does not hold, is a divergence: the publication fails and
// faults the child (OrderedTree.Insert, Remove).
//
// Memory. The nodes are blocks of the arena, counted in its live, prepared and retired bytes and so in
// the budget of the instance. The close frees them with the arena. An entry of a write is made in
// scratch of the writer that the index holds, of the widest entry, allocated when the database opens.
// Readers take no lock; only the holder of the lock of the writes calls the members of a write.
internal sealed class SecondaryIndex
{
    private readonly RootLayout _roots;

    private readonly Arena _arena;

    private readonly IndexOrder _order;

    // The entry a step of a write inserts, removes or rewrites: the writer's alone.
    private readonly byte[] _entry;

    private SecondaryIndex(LatticeIndex definition, RootLayout roots, Arena arena, IndexLayout layout, IndexOrder order, OrderedTree tree)
    {
        Definition = definition;
        _roots = roots;
        _arena = arena;
        _order = order;
        _entry = new byte[layout.WidestEntryBytes];
        Layout = layout;
        Tree = tree;
        IsUnique = definition.IsUnique;
    }

    // The index as the schema declares it.
    public LatticeIndex Definition { get; }

    // Whether the schema declares the index unique.
    public bool IsUnique { get; }

    // How its entries and nodes lie in the arena.
    public IndexLayout Layout { get; }

    // Its tree, which a search descends inside the protection of reads.
    public OrderedTree Tree { get; }

    // The order of its entries, by which a walk tells where it ends (IndexWalk).
    public IndexOrder Order => _order;

    // The index of the declaration for the entity type whose roots have the layout given, empty and so
    // without a node, its nodes in the arena given, its replaced nodes retired into the list given; when
    // the database opens. An index the layout refuses fails the open with the LatticeException of
    // IndexLayout.Compile.
    public static SecondaryIndex Open(LatticeIndex definition, RootLayout roots, Arena arena, RetiredMemory retired)
    {
        var layout = IndexLayout.Compile(definition, roots);
        var order = new IndexOrder(layout, arena);

        return new SecondaryIndex(definition, roots, arena, layout, order, new OrderedTree(layout, order, arena, retired));
    }

    // For a search inside the protection of reads: whether the root, the current state of the entity an
    // entry of the run names, has the key of the search: the order of the index between the two is zero
    // (IndexOrder.HasKey), so that a hint is checked by the order the tree is ordered by. Allocates nothing.
    public bool HasKey(in RootReader root, scoped in SearchKey key) => _order.HasKey(root, key);

    // For a walk inside the protection of reads, whose entries have different keys: whether the root, the
    // current state of the entity an entry of the walk names, still has the key of that entry, by the order
    // of the index, so that a walk returns an entity only at the place of its key and in the order it walks.
    // The entry the root contributes is written on the stack by the writer of entries and compared with the
    // entry as two entries are (IndexLayout.WriteEntry, IndexOrder.SameKey), so the check is the order the
    // tree is ordered by and needs no comparison of its own. Allocates nothing; the scratch is not zeroed,
    // since the entry writes every byte the order reads of it.
    [SkipLocalsInit]
    public bool HasKeyOf(in RootReader root, ReadOnlySpan<byte> entry)
    {
        Span<byte> written = stackalloc byte[Layout.WidestEntryBytes];
        var length = Layout.WriteEntry(root, default, written);

        return _order.SameKey(written[..length], entry);
    }

    // For a walk inside the protection of reads: writes the entry of a node in detached form into the room
    // given (IndexLayout.Detach), with the identity given at its start, where the walk stands at or after it.
    public void DetachAt(ReadOnlySpan<byte> entry, Span<byte> room, RootHandle identity)
    {
        Layout.Detach(_arena, entry, room);
        IndexLayout.WriteIdentity(room, identity);
    }

    // For the writer, in the hold that prepares a replace, once its new root is written: what the replace
    // does to the index, from the root the entity has and the root the write publishes, both the write's
    // own until it is published or withdrawn (see the class). None when they contribute entries of the same
    // bytes (IndexLayout.SameEntry); otherwise Rewrite when their keys are equal by the order, a text under
    // another case or a Decimal of another scale, and Move when they are not (IndexOrder.SameKey). An
    // insert of an entity is an Insert into every index and a delete a Remove. Builds no entry and
    // allocates nothing.
    public IndexChangeEnum ChangeOf(in RootReader replaced, in RootReader root)
    {
        if (Layout.SameEntry(replaced, root))
        {
            return IndexChangeEnum.None;
        }

        return _order.SameKey(replaced, root) ? IndexChangeEnum.Rewrite : IndexChangeEnum.Move;
    }

    // For the writer, in the hold that prepares a write that sets or moves a value of this unique index, once
    // its new root is written: the root entry of the published root that holds the value the root given
    // contributes, true; false when no root holds it, and for a key with a null in any part, which is not
    // checked. Between two publications the tree holds one entry for each live root, under the identity of
    // its root entry, and nothing else, so an entry of the value names its owner. One descent, to the first
    // entry at or after the entry of the value under the lowest identity, compared by the order of the
    // index, which is the comparison of every part; where that place is past the end of a leaf, one more
    // descent at the fence, to the first entry of the leaf beside, as a walk goes on (IndexWalk). The fence
    // is a separator, which is not read as an owner: a separator is a copy of an entry, made when its leaf
    // split, that stays in its inner node when that entry leaves its leaf (OrderedTree.Remove changes only
    // the leaf; only the removal of a whole leaf or a merge takes a separator away), so it may be the copy
    // of a removed entry, or of an entry rewritten since. Writes the entry into the scratch of the writer.
    public bool TryFindOwner(in RootReader root, out RootHandle owner)
    {
        owner = default;

        if (Layout.HasNull(root))
        {
            return false;
        }

        var entry = _entry.AsSpan(0, Layout.WriteEntry(root, default, _entry));
        var place = Tree.Seek(entry, SeekSideEnum.AtOrAfter);

        if (!place.HasEntry && place.HasFence)
        {
            place = Tree.Seek(place.Fence, SeekSideEnum.AtOrAfter);
        }

        if (!place.HasEntry || !_order.SameKey(place.Entry, entry))
        {
            return false;
        }

        owner = IndexLayout.IdentityOf(place.Entry);

        return true;
    }

    // For the writer, under the lock of the writes: whether two roots, prepared or published, contribute
    // one value of this unique index: keys with no null part, equal by the order of the index.
    public bool IsValueOf(in RootReader root, in RootReader other) =>
        !Layout.HasNull(root) && _order.SameKey(root, other);

    // For a child at the position it becomes ready at, under the lock of the writes: whether the tree holds
    // one value of this unique index twice (OrderedTree.HoldsAKeyTwice).
    public bool HoldsAValueTwice() => Tree.HoldsAKeyTwice();

    // For the writer, in the hold that prepares the write: reserves what the change the write makes to
    // the index needs (OrderedTree.TryReserve, CountsOf). False when the arena cannot hold it, with nothing
    // taken (see the class).
    public bool TryReserve(IndexChangeEnum change)
    {
        var (inserts, removes) = CountsOf(change);

        return Tree.TryReserve(inserts, removes);
    }

    // For the writer: gives back what TryReserve took for the change, for a write withdrawn before its
    // publication.
    public void Release(IndexChangeEnum change)
    {
        var (inserts, removes) = CountsOf(change);

        Tree.Release(inserts, removes);
    }

    // For the writer, in the hold that prepares a write, once every index of the write has what the write
    // needs: tops the standing reserve of the tree up, as far as the arena gives (OrderedTree.TopUp).
    public void TopUp() => Tree.TopUp();

    // For the writer, before a write is refused for memory: gives back what the pool of the tree holds
    // above the need of the writes prepared and not published (OrderedTree.GiveBackReserve).
    public void GiveBackReserve() => Tree.GiveBackReserve();

    // For the writer, in the publication of the write, before the stores of its root: adds the entry the
    // root the write publishes contributes, under the handle of the root entry, for an Insert or a Move,
    // from what was reserved. Allocates nothing; does nothing for another change.
    public void InstallNew(IndexChangeEnum change, RootHandle handle, ArenaAddress root)
    {
        if (change is IndexChangeEnum.Insert or IndexChangeEnum.Move)
        {
            Tree.Insert(EntryOf(handle, root));
        }
    }

    // For the writer, in the publication of the write, after its position and before what it replaced
    // or deleted is retired: takes away the entry the replaced root contributes for a Remove or a Move,
    // and for a Rewrite replaces it at its place by the entry of the root the write publishes. Allocates
    // nothing; does nothing for another change.
    public void RemoveOld(IndexChangeEnum change, RootHandle handle, ArenaAddress replaced, ArenaAddress root)
    {
        switch (change)
        {
            case IndexChangeEnum.Remove or IndexChangeEnum.Move:
                Tree.Remove(EntryOf(handle, replaced));
                break;

            case IndexChangeEnum.Rewrite:
                Tree.Rewrite(EntryOf(handle, root));
                break;
        }
    }

    // The inserts and the removes a change of the index takes in its tree: an insert for Insert, Move and
    // Rewrite, a remove for Remove and Move. Inlined, so that a reservation and a release make no call for
    // it (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (int Inserts, int Removes) CountsOf(IndexChangeEnum change) => change switch
    {
        IndexChangeEnum.Insert or IndexChangeEnum.Rewrite => (1, 0),
        IndexChangeEnum.Remove => (0, 1),
        _ => (1, 1),
    };

    // The entry the root contributes under the handle, in the scratch of the writer.
    private ReadOnlySpan<byte> EntryOf(RootHandle handle, ArenaAddress root) =>
        _entry.AsSpan(0, Layout.WriteEntry(new RootReader(_arena, _roots, root), handle, _entry));
}
