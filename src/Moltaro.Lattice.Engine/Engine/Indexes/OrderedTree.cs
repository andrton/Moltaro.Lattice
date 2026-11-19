using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;

namespace Moltaro.Lattice.Engine.Indexes;

// The ordered tree of one secondary index: a B+tree of nodes of the arena whose published nodes never
// change (decision on index structures, the note of 19 November 2026). Modelled on, not adapted from,
// LMDB, the principle of ROWEX and the walk of LeanStore; docs/design/ordered-index.md names them with
// their licences and commits and tells how a split, a merge and a growth go. No code is ported.
//
// Readers. A read inside the protection of reads loads the address of the root, and of each child on
// its way down, with acquire semantics, reads each node with plain loads, and leaves: it takes no lock,
// checks no version and never retries, and it never reads freed memory, since a node a write replaced is
// retired, not freed, until the reads that could reach it have left (Protection.RetiredMemory).
//
// The writer. Only the holder of the lock of the writes changes the tree. A change copies each node it
// changes into a node it reserved before the commit, writes the copy whole (NodeWriter), and publishes
// it by one store of its address with release semantics, into the live parent or into the root of the
// tree; the nodes it replaced are retired after that store. A node holds one reference for each shared
// key among its entries or separators, from the moment it is written until it is freed, as every block
// does (decision on strings, Q04.4, the settlement of 18 November 2026; Storage.Ordered.NodeStorage).
//
// The reservation. Nothing grows inside a publication. While p inserts, rewrites included, and q removes
// are prepared and not published, the tree holds
//
//   p * (2 Hi + 1) + q * (Hr + 1) nodes, with Hi = h + ceil((p - 1) / LeastSeparators)
//   and Hr = h + ceil(p / LeastSeparators),
//
// as prepared blocks no reader can reach, and as many places in the retired list; h is the height now.
// An insert at height H takes at most two nodes on each level and a new root, 2H + 1, and retires at most
// one node on each level; a remove takes at most one on each level and retires at most one more, the
// neighbour of a merge. The height grows only when the root splits, a root made by a split holds one
// separator and an insert adds at most one to any node, so the k-th insert finds the tree at most
// ceil((k - 1) / LeastSeparators) levels above h, and a remove at most ceil(p / LeastSeparators): Hi and
// Hr bound the height at the publications that wait (ordered-index.md, "The reservation"). A preparation
// tops the pool up to the bound; a withdrawal and a publication give back to the arena what is above the
// bound of the writes still waiting, and allocate nothing.
//
// What it holds: the address of its root, a leaf or an inner node, null for an empty tree, which the
// writer stores with release semantics and a reader loads with acquire semantics; its height and count;
// the pool of reserved nodes; the path of the writer's last descent. All but the root are the writer's
// alone.
//
// Who calls what: TryFindFirst from reads, inside the protection; everything else from the writer, under
// the lock of the writes, outside the protection, which is safe because only the holder of that lock
// changes, retires or frees what the tree holds.
internal sealed class OrderedTree
{
    private readonly IndexLayout _layout;

    private readonly IndexOrder _order;

    private readonly Arena _arena;

    private readonly RetiredMemory _retired;

    // The nodes reserved for the publications of the writes prepared and not published, prepared blocks
    // of the arena that no reader can reach.
    private readonly Stack<ArenaAddress> _pool = new();

    // The address of the root as its number, 0 for an empty tree (see the class).
    private ulong _root;

    private int _height;

    private long _count;

    // The live nodes of the tree.
    private long _nodes;

    // The inserts and the removes reserved and not published or withdrawn, and the places in the retired
    // list the tree holds reserved for them.
    private int _inserts;

    private int _removes;

    private int _places;

    // The path of the writer's descent: the node of each level, from the root at 0, and the child it took
    // in each inner node. Grown before a publication, when a reservation lets the tree grow beyond it.
    private ArenaAddress[] _path = [];

    private int[] _slots = [];

    // The nodes one publication replaced, retired after its store.
    private ArenaAddress[] _replaced = [];

    private int _replacedCount;

    // An empty tree of the layout, whose nodes come from the arena and whose replaced nodes go to the
    // retired list given. It holds no node, and nothing is reserved.
    public OrderedTree(IndexLayout layout, IndexOrder order, Arena arena, RetiredMemory retired)
    {
        _layout = layout;
        _order = order;
        _arena = arena;
        _retired = retired;
    }

    // The levels of the tree: 0 when it is empty, 1 when its root is a leaf. For the writer.
    public int Height => _height;

    // The entries of the tree. For the writer and the counters.
    public long Count => _count;

    // The bytes of the live nodes of the tree, slots of the arena; the nodes it retired are counted in
    // the retired bytes of the arena, and the nodes it reserved in its prepared bytes, until they are
    // freed. For the writer and the counters.
    public long Bytes => _nodes * _layout.NodeBytes;

    // The bytes an entry takes in a leaf besides its own: its offset, in an index with a String part.
    private int OffsetBytes => _layout.HasText ? IndexLayout.OffsetBytes : 0;

    // The bytes a leaf holds for its entries and their offsets.
    private int LeafCapacity => _layout.PayloadBytes - NodeHeader.Size;

    // For a read inside the protection of reads: the identity of the first entry at or after the probe,
    // its key and the identity it starts at, when that entry has the key of the probe; false when the run
    // of the key has no entry from there on. One descent from the root to the leaf of the probe, and when
    // the probe lies after the last entry of that leaf, one more from the root at the fence of the leaf,
    // the separator above it in its parent or further up, which every entry of the next leaf is at or
    // above (ordered-index.md, "Fences").
    public bool TryFindFirst(scoped in SearchKey probe, out RootHandle identity)
    {
        identity = default;

        var address = LoadRoot();

        if (address.IsNull)
        {
            return false;
        }

        // The fence: the nearest separator above the leaf on the way down, in a node that stays
        // allocated while the read is inside.
        ReadOnlySpan<byte> fence = default;
        var hasFence = false;
        var node = NodeAt(address);

        while (!node.IsLeaf)
        {
            var child = ChildOf(node, probe);

            if (child < node.Count)
            {
                fence = node.EntryAt(child);
                hasFence = true;
            }

            node = NodeAt(node.ChildAt(child));
        }

        var place = FirstAtOrAfter(node, probe);

        // Every entry of the leaf lies before the probe: the next leaf, whose entries are all at or above
        // the fence, which lies above the probe. A leaf of another moment may end before the fence, and
        // the walk goes on at its own fence; each fence lies above the one before.
        while (place == node.Count)
        {
            if (!hasFence)
            {
                return false;
            }

            var target = fence;

            hasFence = false;
            address = LoadRoot();

            if (address.IsNull)
            {
                return false;
            }

            node = NodeAt(address);

            while (!node.IsLeaf)
            {
                var child = ChildOf(node, target);

                if (child < node.Count)
                {
                    fence = node.EntryAt(child);
                    hasFence = true;
                }

                node = NodeAt(node.ChildAt(child));
            }

            place = FirstAtOrAfter(node, target);
        }

        var entry = node.EntryAt(place);

        if (!_order.SameKey(probe, entry))
        {
            return false;
        }

        identity = IndexLayout.IdentityOf(entry);

        return true;
    }

    // For the writer, in the hold that prepares a write: reserves the nodes and the places in the retired
    // list for the inserts and the removes given, beside those prepared before and not published (see
    // the class, "The reservation"). True when the reservation is made; false when the arena cannot hold
    // the nodes, with nothing taken. An OutOfMemoryException of the native allocator or of the managed
    // heap leaves nothing taken either.
    public bool TryReserve(int inserts, int removes)
    {
        var waitingInserts = _inserts + inserts;
        var waitingRemoves = _removes + removes;
        var nodes = (int)Bound(waitingInserts, waitingRemoves);

        // The managed room first, so that nothing of the arena is taken when it cannot be had.
        GrowPath(waitingInserts);
        _pool.EnsureCapacity(nodes);

        var taken = 0;

        try
        {
            while (_pool.Count < nodes)
            {
                if (!_arena.TryAllocatePrepared(_layout.PayloadBytes, BlockKindEnum.IndexNode, default, out var node))
                {
                    GiveBack(taken);

                    return false;
                }

                _pool.Push(node);
                taken++;
            }

            if (nodes > _places)
            {
                _retired.Reserve(nodes - _places);
                _places = nodes;
            }
        }
        catch
        {
            GiveBack(taken);
            throw;
        }

        _inserts = waitingInserts;
        _removes = waitingRemoves;

        return true;
    }

    // For the writer: gives back what TryReserve took for a write that is withdrawn before its
    // publication; the nodes above what the writes still prepared need go back to the arena.
    public void Release(int inserts, int removes)
    {
        _inserts -= inserts;
        _removes -= removes;
        Trim();
    }

    // For the writer, in a publication: adds the entry, which the tree does not hold, by copies of the
    // nodes it changes from the reservation, each published by one store, and retires what they replace;
    // the text entries its shared keys reference are published before the first store. Allocates nothing.
    // An entry the tree holds already, by its key and identity, is InvalidOperationException before any
    // store: on a master a defect, on a child the stream contradicting its index, which faults it.
    public void Insert(ReadOnlySpan<byte> entry)
    {
        if (_root == 0)
        {
            TakeInsert();
            PublishTexts(entry);

            var leaf = TakeNode();
            var writer = new NodeWriter(_layout, PayloadOf(leaf), 0, 1);

            writer.Add(entry);
            Complete(ref writer, leaf);
            StoreRoot(leaf);
            _height = 1;
        }
        else
        {
            var level = Descend(entry);
            var leafAddress = _path[level];
            var leaf = NodeAt(leafAddress);
            var place = FirstAtOrAfter(leaf, entry);

            if (place < leaf.Count && _order.Compare(entry, leaf.EntryAt(place)) == 0)
            {
                throw new InvalidOperationException($"The index {_layout.Index.Name} holds the entry it is to add already: the entity of the identity {IndexLayout.IdentityOf(entry)} has it under this key.");
            }

            TakeInsert();
            PublishTexts(entry);

            var edited = new EditedLeaf(leaf, place, entry, EditedLeaf.Inserts);

            // An entry after the last of the last leaf, which does not fit it, leaves the full leaf as it
            // is and starts a new one, so that ascending keys leave full leaves, as LMDB biases its split.
            if (!Fits(edited) && place == leaf.Count && IsRightmost(level))
            {
                var alone = TakeNode();
                var writer = new NodeWriter(_layout, PayloadOf(alone), 0, 1);

                writer.Add(entry);
                Complete(ref writer, alone);
                InsertIntoParent(level - 1, leafAddress, alone, entry);
            }
            else
            {
                PlaceLeaf(level, leafAddress, edited);
            }
        }

        _count++;
        AfterPublication();
    }

    // For the writer, in a publication: takes the entry away, as Insert adds one. An entry the tree does
    // not hold is InvalidOperationException before any store, as for Insert.
    public void Remove(ReadOnlySpan<byte> entry)
    {
        var level = FindHeld(entry, out var place);

        if (_removes == 0)
        {
            throw new InvalidOperationException($"A remove from the index {_layout.Index.Name} is published that reserved nothing.");
        }

        _removes--;

        var leafAddress = _path[level];
        var leaf = NodeAt(leafAddress);

        if (leaf.Count == 1)
        {
            // A leaf left empty goes away from its parent; a tree left without entries has no node.
            Replaced(leafAddress);

            if (level == 0)
            {
                StoreRoot(default);
                _height = 0;
            }
            else
            {
                RemoveChild(level - 1);
            }
        }
        else
        {
            var edited = new EditedLeaf(leaf, place, default, EditedLeaf.Removes);

            if (level == 0 || BytesOf(edited) >= LeafCapacity / 4 || !TryMergeLeaf(level, leafAddress, edited))
            {
                PlaceLeaf(level, leafAddress, edited);
            }
        }

        _count--;
        AfterPublication();
    }

    // For the writer, in a publication: replaces the entry the tree holds at the place of this one, the
    // same key by the order and the same identity in other bytes, by a copy of its leaf, or a split when
    // the new bytes do not fit; reserved as an insert. An entry with no equal is InvalidOperationException
    // before any store, as for Remove.
    public void Rewrite(ReadOnlySpan<byte> entry)
    {
        var level = FindHeld(entry, out var place);

        TakeInsert();
        PublishTexts(entry);
        PlaceLeaf(level, _path[level], new EditedLeaf(NodeAt(_path[level]), place, entry, EditedLeaf.Replaces));
        AfterPublication();
    }

    // For the tests of the engine (EngineDatabase.IndexNodes): every live node, in the order of a walk
    // from the root, a node before its children and the children of an inner node in their order, with
    // its level and a copy of each of its entries or separators. Under the lock of the writes.
    public List<(int Level, List<byte[]> Items)> Nodes()
    {
        var nodes = new List<(int Level, List<byte[]> Items)>();

        if (_root != 0)
        {
            Walk(LoadRoot(), nodes);
        }

        return nodes;
    }

    private void Walk(ArenaAddress address, List<(int Level, List<byte[]> Items)> nodes)
    {
        var node = NodeAt(address);
        var items = new List<byte[]>(node.Count);

        for (var place = 0; place < node.Count; place++)
        {
            items.Add(node.EntryAt(place).ToArray());
        }

        nodes.Add((node.Level, items));

        if (!node.IsLeaf)
        {
            for (var child = 0; child <= node.Count; child++)
            {
                Walk(node.ChildAt(child), nodes);
            }
        }
    }

    // The nodes the waiting publications can take (see the class, "The reservation").
    private long Bound(int inserts, int removes)
    {
        var separators = _layout.LeastSeparators;
        var insertHeight = _height + CeilingOf(Math.Max(inserts - 1, 0), separators);
        var removeHeight = _height + CeilingOf(inserts, separators);

        return (inserts * ((2L * insertHeight) + 1)) + (removes * (removeHeight + 1L));
    }

    private static long CeilingOf(int dividend, int divisor) => (dividend + divisor - 1) / divisor;

    // Room in the writer's path for the height the waiting inserts can reach, a new root above it
    // included, and for the nodes one publication replaces, at most one more than the levels.
    private void GrowPath(int inserts)
    {
        var levels = (int)(_height + 2 + CeilingOf(inserts, _layout.LeastSeparators));

        if (_path.Length < levels)
        {
            _path = new ArenaAddress[levels];
            _slots = new int[levels];
            _replaced = new ArenaAddress[levels + 1];
        }
    }

    // Frees the nodes taken by a reservation that failed.
    private void GiveBack(int taken)
    {
        for (var node = 0; node < taken; node++)
        {
            _arena.Free(_pool.Pop());
        }
    }

    // After a withdrawal or a publication: what is above the bound of the writes still waiting goes back,
    // the nodes to the arena and the places to the retired list (see the class).
    private void Trim()
    {
        var bound = Bound(_inserts, _removes);

        while (_pool.Count > bound)
        {
            _arena.Free(_pool.Pop());
        }

        if (_places > bound)
        {
            _retired.Release(_places - (int)bound);
            _places = (int)bound;
        }
    }

    private void TakeInsert()
    {
        if (_inserts == 0)
        {
            throw new InvalidOperationException($"An insert into the index {_layout.Index.Name} is published that reserved nothing.");
        }

        _inserts--;
    }

    // The end of a publication: the nodes its store replaced are retired, and what the writes still
    // waiting do not need goes back.
    private void AfterPublication()
    {
        for (var index = 0; index < _replacedCount; index++)
        {
            _retired.RetireNode(_layout, _replaced[index]);
            _nodes--;
            _places--;
        }

        _replacedCount = 0;
        Trim();
    }

    // The leaf of the entry, which the tree holds by its key and its identity, with its place in it;
    // InvalidOperationException before any store when it holds no such entry.
    private int FindHeld(ReadOnlySpan<byte> entry, out int place)
    {
        if (_root != 0)
        {
            var level = Descend(entry);
            var leaf = NodeAt(_path[level]);

            place = FirstAtOrAfter(leaf, entry);

            if (place < leaf.Count && _order.Compare(entry, leaf.EntryAt(place)) == 0)
            {
                return level;
            }
        }

        throw new InvalidOperationException($"The index {_layout.Index.Name} does not hold the entry it is to change: the entity of the identity {IndexLayout.IdentityOf(entry)} has no entry under this key.");
    }

    // The writer's descent to the leaf of the entry, which records the path: returns the level of the
    // leaf, the height less one. Only the writer changes the tree, so it reads it without the protection.
    private int Descend(ReadOnlySpan<byte> entry)
    {
        var address = LoadRoot();

        for (var level = 0; ; level++)
        {
            _path[level] = address;

            var node = NodeAt(address);

            if (node.IsLeaf)
            {
                return level;
            }

            var slot = ChildOf(node, entry);

            _slots[level] = slot;
            address = node.ChildAt(slot);
        }
    }

    // Whether every node above the level on the path took its last child: the node of the level is the
    // last of its level.
    private bool IsRightmost(int level)
    {
        for (var above = 0; above < level; above++)
        {
            if (_slots[above] != NodeAt(_path[above]).Count)
            {
                return false;
            }
        }

        return true;
    }

    // The leaf of the level, changed: published by a copy when it fits a node, otherwise split in the
    // middle of its bytes into two leaves, the first entry of the second the separator between them.
    private void PlaceLeaf(int level, ArenaAddress leafAddress, scoped in EditedLeaf edited)
    {
        Replaced(leafAddress);

        if (Fits(edited))
        {
            Replace(level, WriteLeaf(edited, 0, edited.Count));

            return;
        }

        var split = SplitOf(edited);
        var left = WriteLeaf(edited, 0, split);
        var right = WriteLeaf(edited, split, edited.Count);

        InsertIntoParent(level - 1, left, right, NodeAt(right).EntryAt(0));
    }

    private bool Fits(scoped in EditedLeaf edited) => BytesOf(edited) <= LeafCapacity;

    // The bytes the entries of the leaf take with their offsets.
    private int BytesOf(scoped in EditedLeaf edited)
    {
        var bytes = 0;

        for (var place = 0; place < edited.Count; place++)
        {
            bytes += edited.At(place).Length + OffsetBytes;
        }

        return bytes;
    }

    // Where a leaf that does not fit splits: the first entry of the second half, so that the first holds
    // half of the bytes or less, and each holds one entry at least. Each half fits a node, since a leaf
    // holds four of the widest entries at least (IndexLayout.LeastEntriesPerNode).
    private int SplitOf(scoped in EditedLeaf edited)
    {
        var half = BytesOf(edited) / 2;
        var bytes = 0;
        var split = 0;

        while (split < edited.Count - 1 && bytes + edited.At(split).Length + OffsetBytes <= half)
        {
            bytes += edited.At(split).Length + OffsetBytes;
            split++;
        }

        return Math.Max(split, 1);
    }

    // Writes the entries from first to end of the edited leaf into a new leaf, and returns it.
    private ArenaAddress WriteLeaf(scoped in EditedLeaf edited, int first, int end)
    {
        var leaf = TakeNode();
        var writer = new NodeWriter(_layout, PayloadOf(leaf), 0, end - first);

        for (var place = first; place < end; place++)
        {
            writer.Add(edited.At(place));
        }

        Complete(ref writer, leaf);

        return leaf;
    }

    // The child that the path took in the inner node of the level was split into left and right, divided
    // by the separator: the node gets one separator and one child more, by a copy, or is split itself and
    // gives its middle separator to its parent; above the root, a new root takes the two (ordered-index.md,
    // "Writes"). Left may be the child itself, unchanged, when only right is new.
    private void InsertIntoParent(int level, ArenaAddress left, ArenaAddress right, ReadOnlySpan<byte> separator)
    {
        if (level < 0)
        {
            var root = TakeNode();
            var writer = new NodeWriter(_layout, PayloadOf(root), _height, 1);

            writer.AddChild(left);
            writer.AddChild(right);
            writer.Add(separator);
            Complete(ref writer, root);
            StoreRoot(root);
            _height++;

            return;
        }

        var parentAddress = _path[level];
        var grown = new GrownInner(NodeAt(parentAddress), _slots[level], separator, left, right);

        Replaced(parentAddress);

        if (InnerBytesOf(grown, 0, grown.Count) <= _layout.PayloadBytes - NodeHeader.Size - IndexLayout.ChildBytes)
        {
            Replace(level, WriteInner(grown, 0, grown.Count));

            return;
        }

        var middle = InnerSplitOf(grown);
        var newLeft = WriteInner(grown, 0, middle);
        var newRight = WriteInner(grown, middle + 1, grown.Count);

        InsertIntoParent(level - 1, newLeft, newRight, grown.SeparatorAt(middle));
    }

    // The bytes the separators from first to end of an inner node take with their offsets and the child
    // after each; the first child is not counted.
    private int InnerBytesOf(scoped in GrownInner grown, int first, int end)
    {
        var bytes = 0;

        for (var place = first; place < end; place++)
        {
            bytes += grown.SeparatorAt(place).Length + OffsetBytes + IndexLayout.ChildBytes;
        }

        return bytes;
    }

    // The separator that goes up when an inner node splits: one in the middle of the bytes, with one
    // separator at least on each side. A full inner node holds four of the widest separators at least, so
    // each half fits.
    private int InnerSplitOf(scoped in GrownInner grown)
    {
        var half = InnerBytesOf(grown, 0, grown.Count) / 2;
        var bytes = 0;
        var middle = 0;

        while (middle < grown.Count - 2 && bytes + grown.SeparatorAt(middle).Length + OffsetBytes + IndexLayout.ChildBytes <= half)
        {
            bytes += grown.SeparatorAt(middle).Length + OffsetBytes + IndexLayout.ChildBytes;
            middle++;
        }

        return Math.Max(middle, 1);
    }

    // Writes the separators from first to end of the grown inner node, with the children from first to
    // end, into a new inner node, and returns it.
    private ArenaAddress WriteInner(scoped in GrownInner grown, int first, int end)
    {
        var inner = TakeNode();
        var writer = new NodeWriter(_layout, PayloadOf(inner), grown.Level, end - first);

        for (var place = first; place < end; place++)
        {
            writer.Add(grown.SeparatorAt(place));
        }

        for (var child = first; child <= end; child++)
        {
            writer.AddChild(grown.ChildAt(child));
        }

        Complete(ref writer, inner);

        return inner;
    }

    // A leaf of the level left with few bytes after a remove merges with a neighbour under the same
    // parent when both fit three quarters of a leaf, and the parent loses the separator between them;
    // false, with nothing changed, when no neighbour is small enough.
    private bool TryMergeLeaf(int level, ArenaAddress leafAddress, scoped in EditedLeaf edited)
    {
        var parent = NodeAt(_path[level - 1]);
        var slot = _slots[level - 1];
        var neighbourSlot = slot > 0 ? slot - 1 : slot < parent.Count ? slot + 1 : -1;

        if (neighbourSlot < 0)
        {
            return false;
        }

        var neighbourAddress = parent.ChildAt(neighbourSlot);
        var neighbour = NodeAt(neighbourAddress);

        if (neighbour.UsedBytes + BytesOf(edited) > 3 * LeafCapacity / 4)
        {
            return false;
        }

        var merged = TakeNode();
        var writer = new NodeWriter(_layout, PayloadOf(merged), 0, neighbour.Count + edited.Count);

        if (neighbourSlot < slot)
        {
            AddEntries(ref writer, neighbour);
        }

        for (var place = 0; place < edited.Count; place++)
        {
            writer.Add(edited.At(place));
        }

        if (neighbourSlot > slot)
        {
            AddEntries(ref writer, neighbour);
        }

        Complete(ref writer, merged);
        Replaced(leafAddress);
        Replaced(neighbourAddress);
        ReplaceChildPair(level - 1, Math.Min(slot, neighbourSlot), merged);

        return true;
    }

    private static void AddEntries(ref NodeWriter writer, scoped in IndexNode node)
    {
        for (var place = 0; place < node.Count; place++)
        {
            writer.Add(node.EntryAt(place));
        }
    }

    // The children first and first + 1 of the inner node of the level became the one merged node: the
    // node is copied without the separator between them; a root left with one child gives way to it.
    private void ReplaceChildPair(int level, int first, ArenaAddress merged)
    {
        var parentAddress = _path[level];
        var parent = NodeAt(parentAddress);

        Replaced(parentAddress);

        if (parent.Count == 1 && level == 0)
        {
            StoreRoot(merged);
            _height--;

            return;
        }

        var copy = TakeNode();
        var writer = new NodeWriter(_layout, PayloadOf(copy), parent.Level, parent.Count - 1);

        for (var place = 0; place < parent.Count; place++)
        {
            if (place != first)
            {
                writer.Add(parent.EntryAt(place));
            }
        }

        for (var child = 0; child <= parent.Count; child++)
        {
            if (child == first)
            {
                writer.AddChild(merged);
            }
            else if (child != first + 1)
            {
                writer.AddChild(parent.ChildAt(child));
            }
        }

        Complete(ref writer, copy);
        Replace(level, copy);
    }

    // The child that the path took in the inner node of the level goes away: the node is copied without
    // it and without the separator below it, or above it for the first child; a node left without
    // children goes away from its own parent, and a root left with one child gives way to it.
    private void RemoveChild(int level)
    {
        var parentAddress = _path[level];
        var parent = NodeAt(parentAddress);
        var slot = _slots[level];

        Replaced(parentAddress);

        if (parent.Count == 0)
        {
            if (level == 0)
            {
                StoreRoot(default);
                _height = 0;
            }
            else
            {
                RemoveChild(level - 1);
            }

            return;
        }

        if (parent.Count == 1 && level == 0)
        {
            StoreRoot(parent.ChildAt(1 - slot));
            _height--;

            return;
        }

        var removed = slot > 0 ? slot - 1 : 0;
        var copy = TakeNode();
        var writer = new NodeWriter(_layout, PayloadOf(copy), parent.Level, parent.Count - 1);

        for (var place = 0; place < parent.Count; place++)
        {
            if (place != removed)
            {
                writer.Add(parent.EntryAt(place));
            }
        }

        for (var child = 0; child <= parent.Count; child++)
        {
            if (child != slot)
            {
                writer.AddChild(parent.ChildAt(child));
            }
        }

        Complete(ref writer, copy);
        Replace(level, copy);
    }

    // The one store that publishes a new node of the level: into its parent on the path, which is live
    // and not copied, or into the root of the tree.
    private void Replace(int level, ArenaAddress node)
    {
        if (level == 0)
        {
            StoreRoot(node);
        }
        else
        {
            NodeAt(_path[level - 1]).PublishChild(_slots[level - 1], node);
        }
    }

    private void Replaced(ArenaAddress node) => _replaced[_replacedCount++] = node;

    // A node of the reservation, live from now on and counted with the tree; nothing reaches it before
    // the store that publishes it.
    private ArenaAddress TakeNode()
    {
        if (!_pool.TryPop(out var node))
        {
            throw new InvalidOperationException($"A publication of the index {_layout.Index.Name} took more nodes than were reserved for it.");
        }

        _arena.Publish(node);
        _nodes++;

        return node;
    }

    // Writes the header of a node the writer has filled, and takes the references of its shared keys.
    private void Complete(ref NodeWriter writer, ArenaAddress node)
    {
        writer.Complete();
        NodeStorage.TakeReferences(_arena, _layout, node);
    }

    // The text entries the shared keys of an entry to be added reference are published before the node
    // that holds the entry: they belong to a root that is published after it (Entities.ChangePublisher).
    private void PublishTexts(ReadOnlySpan<byte> entry)
    {
        if (!_layout.HasText)
        {
            return;
        }

        Span<ArenaAddress> texts = stackalloc ArenaAddress[_layout.TextParts];
        var count = _layout.SharedTexts(entry, texts);

        for (var text = 0; text < count; text++)
        {
            _arena.Texts.Publish(texts[text]);
        }
    }

    // The root, loaded with acquire semantics (see the class).
    private ArenaAddress LoadRoot()
    {
        var root = Volatile.Read(ref _root);

        return Unsafe.As<ulong, ArenaAddress>(ref root);
    }

    // The store that publishes a new root, with release semantics.
    private void StoreRoot(ArenaAddress root) => Volatile.Write(ref _root, root.Value);

    private Span<byte> PayloadOf(ArenaAddress node) => _arena.Payload(node);

    private IndexNode NodeAt(ArenaAddress node) => new(_layout, _arena.Payload(node));

    // The child of an inner node whose range holds the probe: the number of separators at or below it.
    private int ChildOf(scoped in IndexNode node, scoped in SearchKey probe)
    {
        var low = 0;
        var high = node.Count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.Compare(probe, node.EntryAt(middle)) < 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    // The same for an entry: for the writer, and for a reader that descends at a fence.
    private int ChildOf(scoped in IndexNode node, ReadOnlySpan<byte> entry)
    {
        var low = 0;
        var high = node.Count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.Compare(entry, node.EntryAt(middle)) < 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    // The place of the first entry of a leaf at or after the probe: the number of entries below it.
    private int FirstAtOrAfter(scoped in IndexNode leaf, scoped in SearchKey probe)
    {
        var low = 0;
        var high = leaf.Count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.Compare(probe, leaf.EntryAt(middle)) <= 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    // The same for an entry.
    private int FirstAtOrAfter(scoped in IndexNode leaf, ReadOnlySpan<byte> entry)
    {
        var low = 0;
        var high = leaf.Count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.Compare(entry, leaf.EntryAt(middle)) <= 0)
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    // The entries of a leaf with one change, as the copy that publishes the change holds them: one entry
    // added at a place, replaced at a place, or taken away from a place.
    private readonly ref struct EditedLeaf
    {
        public const int Inserts = 1;

        public const int Replaces = 0;

        public const int Removes = -1;

        private readonly IndexNode _leaf;

        private readonly int _place;

        private readonly ReadOnlySpan<byte> _entry;

        private readonly int _change;

        public EditedLeaf(IndexNode leaf, int place, ReadOnlySpan<byte> entry, int change)
        {
            _leaf = leaf;
            _place = place;
            _entry = entry;
            _change = change;
        }

        public int Count => _leaf.Count + _change;

        public ReadOnlySpan<byte> At(int place) => _change switch
        {
            Inserts => place < _place ? _leaf.EntryAt(place) : place == _place ? _entry : _leaf.EntryAt(place - 1),
            Replaces => place == _place ? _entry : _leaf.EntryAt(place),
            _ => place < _place ? _leaf.EntryAt(place) : _leaf.EntryAt(place + 1),
        };
    }

    // The separators and children of an inner node whose child at a place was split into left and right
    // by a separator: one separator more at that place, and the two in place of the child.
    private readonly ref struct GrownInner
    {
        private readonly IndexNode _node;

        private readonly int _slot;

        private readonly ReadOnlySpan<byte> _separator;

        private readonly ArenaAddress _left;

        private readonly ArenaAddress _right;

        public GrownInner(IndexNode node, int slot, ReadOnlySpan<byte> separator, ArenaAddress left, ArenaAddress right)
        {
            _node = node;
            _slot = slot;
            _separator = separator;
            _left = left;
            _right = right;
        }

        // The separators; the children are one more.
        public int Count => _node.Count + 1;

        public int Level => _node.Level;

        public ReadOnlySpan<byte> SeparatorAt(int place) =>
            place < _slot ? _node.EntryAt(place) : place == _slot ? _separator : _node.EntryAt(place - 1);

        public ArenaAddress ChildAt(int place) =>
            place < _slot ? _node.ChildAt(place) : place == _slot ? _left : place == _slot + 1 ? _right : _node.ChildAt(place - 1);
    }
}
