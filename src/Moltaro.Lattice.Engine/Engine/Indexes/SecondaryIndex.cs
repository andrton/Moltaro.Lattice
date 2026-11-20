using Moltaro.Lattice.Engine.Comparison;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

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
// A write. ChangeOf compares the entry of the state the entity has with the entry of the state the
// write publishes; nothing changes either root between the preparation and the publication of the write
// (Writes.WriteCoordinator), so each step computes the change again instead of recording it. What the
// place where the effects of a write are installed calls, for each index of the entity type, under the
// lock of the writes, on a master and on a child alike:
// - in the hold that prepares the write: TryReserve; false when the arena cannot hold what the change
//   needs, which refuses the write as one the memory budget cannot hold, with nothing taken; then, once
//   every index has what the write needs, TopUp, which tops the standing reserve up and never fails;
// - when the write is withdrawn before its publication: Release;
// - in its publication, before the stores that publish the root and the position: InstallNew;
// - after the position, before the old root or the deleted entry is retired: RemoveOld.
// So the entries of one publication are installed in this order: the new entries, then the root, then
// the old entries go; each by its own stores, and the check of a found entry against its root covers
// the moments between. A child that applies a transaction of several effects installs the new entries
// of every effect before its position and removes the old ones after it.
//
// A child builds its indexes while it loads, an entry for each root it places, through the same members,
// before it is ready, and keeps them while it follows. An entry its stream contradicts, one to add that
// the index holds or one to take away that it does not hold, is a divergence: the publication fails and
// faults the child (OrderedTree.Insert, Remove).
//
// Memory. The nodes are blocks of the arena, counted in its live, prepared and retired bytes and so in
// the budget of the instance; the bytes of the live ones are Tree.Bytes. The close frees them with the
// arena. The two entries of a write are made in scratch of the writer that the index holds, of the
// widest entry each, allocated when the database opens. Readers take no lock; only the holder of the
// lock of the writes calls the members of a write.
internal sealed class SecondaryIndex
{
    private readonly RootLayout _roots;

    private readonly Arena _arena;

    private readonly IndexOrder _order;

    // The entries of the old and of the new state of the entity of a write: the writer's alone.
    private readonly byte[] _old;

    private readonly byte[] _new;

    private SecondaryIndex(LatticeIndex definition, RootLayout roots, Arena arena, IndexLayout layout, IndexOrder order, OrderedTree tree)
    {
        Definition = definition;
        _roots = roots;
        _arena = arena;
        _order = order;
        _old = new byte[layout.WidestEntryBytes];
        _new = new byte[layout.WidestEntryBytes];
        Layout = layout;
        Tree = tree;
    }

    // The index as the schema declares it.
    public LatticeIndex Definition { get; }

    // How its entries and nodes lie in the arena.
    public IndexLayout Layout { get; }

    // Its tree, which a search descends inside the protection of reads.
    public OrderedTree Tree { get; }

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
    // entry of the run names, has the key of the search, by the order of the index (see the class): each
    // part null in both, or a value in both that the comparison of its field holds equal to the key's.
    // Allocates nothing.
    public bool HasKey(in RootReader root, scoped in SearchKey key)
    {
        var parts = Layout.Parts;
        var keyOffset = SearchKey.FixedStart;

        for (var place = 0; place < parts.Length; place++)
        {
            ref readonly var part = ref parts[place];
            var rootNull = root.IsNull(part.Field);
            var keyNull = key.IsNull(place);

            if (rootNull || keyNull)
            {
                if (rootNull != keyNull)
                {
                    return false;
                }
            }
            else if (part.IsText)
            {
                if (!ValueComparer.TextEquals(root, part.Field, key.TextAt(place)))
                {
                    return false;
                }
            }
            else if (ValueComparer.CompareStored(part.Type, root.StoredBytes(part.Field), key.FixedAt(keyOffset, part.Width)) != 0)
            {
                return false;
            }

            if (!part.IsText)
            {
                keyOffset += part.Width;
            }
        }

        return true;
    }

    // For the writer: what the write does to the index, from the root the entity has, null for an
    // insert, and the root the write publishes, null for a delete; both are the write's own until it is
    // published or withdrawn.
    public IndexChangeEnum ChangeOf(ArenaAddress replaced, ArenaAddress root) => Change(default, replaced, root, out _, out _);

    // For the writer, in the hold that prepares the write: reserves what the change of the write needs
    // (OrderedTree.TryReserve): an insert for Insert, Move and Rewrite, a remove for Remove and Move.
    // False when the arena cannot hold it, with nothing taken (see the class).
    public bool TryReserve(ArenaAddress replaced, ArenaAddress root) => ChangeOf(replaced, root) switch
    {
        IndexChangeEnum.None => true,
        IndexChangeEnum.Insert or IndexChangeEnum.Rewrite => Tree.TryReserve(inserts: 1, removes: 0),
        IndexChangeEnum.Remove => Tree.TryReserve(inserts: 0, removes: 1),
        _ => Tree.TryReserve(inserts: 1, removes: 1),
    };

    // For the writer, in the hold that prepares the write, once every index of the write has its need: tops
    // the pool of the tree up to its standing reserve when the write changes the index
    // (OrderedTree.TopUp). Nothing fails.
    public void TopUp(ArenaAddress replaced, ArenaAddress root)
    {
        if (ChangeOf(replaced, root) != IndexChangeEnum.None)
        {
            Tree.TopUp();
        }
    }

    // For the writer: gives back what TryReserve took, for a write withdrawn before its publication.
    public void Release(ArenaAddress replaced, ArenaAddress root)
    {
        switch (ChangeOf(replaced, root))
        {
            case IndexChangeEnum.Insert or IndexChangeEnum.Rewrite:
                Tree.Release(inserts: 1, removes: 0);
                break;

            case IndexChangeEnum.Remove:
                Tree.Release(inserts: 0, removes: 1);
                break;

            case IndexChangeEnum.Move:
                Tree.Release(inserts: 1, removes: 1);
                break;
        }
    }

    // For the writer, in the publication of the write, before the stores of its root: adds the new entry
    // of an Insert or a Move, under the handle of the root entry, from what was reserved. Allocates
    // nothing; does nothing for another change.
    public void InstallNew(RootHandle handle, ArenaAddress replaced, ArenaAddress root)
    {
        if (Change(handle, replaced, root, out _, out var newLength) is IndexChangeEnum.Insert or IndexChangeEnum.Move)
        {
            Tree.Insert(_new.AsSpan(0, newLength));
        }
    }

    // For the writer, in the publication of the write, after its position and before what it replaced
    // or deleted is retired: takes away the old entry of a Remove or a Move, and replaces the entry of a
    // Rewrite by the new bytes. Allocates nothing; does nothing for another change.
    public void RemoveOld(RootHandle handle, ArenaAddress replaced, ArenaAddress root)
    {
        switch (Change(handle, replaced, root, out var oldLength, out var newLength))
        {
            case IndexChangeEnum.Remove or IndexChangeEnum.Move:
                Tree.Remove(_old.AsSpan(0, oldLength));
                break;

            case IndexChangeEnum.Rewrite:
                Tree.Rewrite(_new.AsSpan(0, newLength));
                break;
        }
    }

    // The entry of each state under the handle, in the scratch of the writer, and what the write does
    // with them: the same bytes, nothing; equal keys by the order in other bytes, a rewrite; another key,
    // a move.
    private IndexChangeEnum Change(RootHandle handle, ArenaAddress replaced, ArenaAddress root, out int oldLength, out int newLength)
    {
        oldLength = replaced.IsNull ? 0 : Layout.WriteEntry(new RootReader(_arena, _roots, replaced), handle, _old);
        newLength = root.IsNull ? 0 : Layout.WriteEntry(new RootReader(_arena, _roots, root), handle, _new);

        if (replaced.IsNull)
        {
            return IndexChangeEnum.Insert;
        }

        if (root.IsNull)
        {
            return IndexChangeEnum.Remove;
        }

        var old = _old.AsSpan(0, oldLength);
        var current = _new.AsSpan(0, newLength);

        if (old.SequenceEqual(current))
        {
            return IndexChangeEnum.None;
        }

        return _order.SameKey(old, current) ? IndexChangeEnum.Rewrite : IndexChangeEnum.Move;
    }
}
