using System.Diagnostics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Indexes;

// The ordered tree of one secondary index: a B+tree of nodes of the arena whose published nodes never
// change (decision on index structures, the note of 19 November 2026). Modelled on, not adapted from,
// LMDB, the principle of ROWEX and the walk of LeanStore; docs/design/ordered-index.md names them with
// their licences and commits and tells how a descent, a split, a merge and a growth go. No code is ported.
//
// What it guarantees. A read inside the protection of reads takes no lock, checks no version, never
// retries and never reads freed memory: it loads the root and every child address with acquire semantics,
// and a node a write replaced is retired, not freed, until the reads that could reach it have left
// (Protection.RetiredMemory; ordered-index.md, "How a reader descends, and why it is safe"). A write
// copies each node it changes into a node reserved before its commit, writes the copy whole (NodeWriter),
// publishes it by one store of its address with release semantics and retires what it replaced after that
// store. A node holds one reference for each shared key among its entries and separators while it is
// allocated (Storage.Ordered.NodeStorage; ordered-index.md, "Text references"). Nothing grows inside a
// publication: the pool holds the need of the writes prepared and not published (Need), and once a
// preparation has touched the index, the standing reserve of one change of key (StandingReserve, TopUp,
// GiveBackReserve), which is never the reason of a refusal (ordered-index.md, "The reservation", "The
// standing reserve"). A Debug build asserts after every change of the pool that it holds the need of the
// writes still waiting (AssertReserved).
//
// What it holds: the address of its root, a leaf or an inner node, null for an empty tree, which the
// writer stores with release semantics and a reader loads with acquire semantics; its height; the pool
// of reserved nodes; the path of the writer's last descent. Seek runs from reads, inside
// the protection; everything else from the writer, under the lock of the writes, which is safe because
// only the holder of that lock changes, retires or frees what the tree holds, and all but the root is its
// alone.
internal sealed class OrderedTree
{
    private readonly IndexLayout _layout;

    private readonly IndexOrder _order;

    private readonly Arena _arena;

    private readonly RetiredMemory _retired;

    // The nodes reserved for the publications of the writes prepared and not published, and the standing
    // reserve: prepared blocks of the arena that no reader can reach.
    private readonly Stack<ArenaAddress> _pool = new();

    // The address of the root as its number, 0 for an empty tree (see the class).
    private ulong _root;

    private int _height;

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

    // The levels of the tree: 0 when it is empty, 1 when its root is a leaf. The writer computes the need
    // of a reservation from it (Need); the tests of the engine hold that it is the height the nodes of the
    // tree have.
    public int Height => _height;

    // The bytes an entry takes in a leaf besides its own: its offset, in an index with a String part.
    private int OffsetBytes => _layout.HasText ? IndexLayout.OffsetBytes : 0;

    // The bytes a leaf holds for its entries and their offsets.
    private int LeafCapacity => _layout.PayloadBytes - NodeHeader.Size;

    // For a read inside the protection of reads: the one descent of a reader, in either direction, from the
    // root to the leaf whose range holds the probe, with the place of the first entry at or after the probe
    // or of the last entry before it, as the side says, and the fence of that leaf on that side, the nearest
    // separator beside the path on it, which lies in a node that stays allocated while the read is inside
    // (TreePlace; ordered-index.md, "Fences"). It descends once: a place beyond the leaf, after its last
    // entry or before its first, is the walk's to follow at the fence on that side. For the last entry
    // before the probe the descent takes, at each inner node, the child left of a separator equal to the
    // probe, since every entry at or after that separator lies at or after the probe. Default for a tree
    // that holds no entry.
    public TreePlace Seek(scoped in SearchKey probe, SeekSideEnum side)
    {
        var address = LoadRoot();

        if (address.IsNull)
        {
            return default;
        }

        var before = side == SeekSideEnum.Before;
        ReadOnlySpan<byte> fence = default;
        var hasFence = false;
        var node = NodeAt(address);

        while (!node.IsLeaf)
        {
            var child = EntriesBelow(node, probe, equalBelow: !before);

            // The fence on the side of the descent: the separator left of the child for a descent before
            // the probe, right of it for one at or after it.
            if (before && child > 0)
            {
                fence = node.EntryAt(child - 1);
                hasFence = true;
            }
            else if (!before && child < node.Count)
            {
                fence = node.EntryAt(child);
                hasFence = true;
            }

            node = NodeAt(node.ChildAt(child));
        }

        var place = EntriesBelow(node, probe, equalBelow: false) - (before ? 1 : 0);

        return new TreePlace(node, place, fence, hasFence);
    }

    // The same descent by a whole entry, its identity included: a fence of a leaf, at which a walk goes on
    // to the leaf beside, or the entry a walk stands after, in detached form (IndexLayout.Detach). Written
    // a second time, for the kind of its probe, as the binary search of a node is (EntriesBelow).
    public TreePlace Seek(ReadOnlySpan<byte> entry, SeekSideEnum side)
    {
        var address = LoadRoot();

        if (address.IsNull)
        {
            return default;
        }

        var before = side == SeekSideEnum.Before;
        ReadOnlySpan<byte> fence = default;
        var hasFence = false;
        var node = NodeAt(address);

        while (!node.IsLeaf)
        {
            var child = EntriesBelow(node, entry, equalBelow: !before);

            // The fence on the side of the descent: the separator left of the child for a descent before
            // the probe, right of it for one at or after it.
            if (before && child > 0)
            {
                fence = node.EntryAt(child - 1);
                hasFence = true;
            }
            else if (!before && child < node.Count)
            {
                fence = node.EntryAt(child);
                hasFence = true;
            }

            node = NodeAt(node.ChildAt(child));
        }

        var place = EntriesBelow(node, entry, equalBelow: false) - (before ? 1 : 0);

        return new TreePlace(node, place, fence, hasFence);
    }

    // For the writer, in the hold that prepares a write: reserves the nodes and the places in the retired
    // list for the inserts and the removes given, beside those prepared before and not published, the
    // need of them all (ordered-index.md, "The reservation"); the standing reserve is TopUp's. True when the
    // reservation is made; false when the arena cannot hold the nodes, or the pool cannot count them, with
    // nothing taken. An OutOfMemoryException of the native allocator or of the managed heap leaves nothing
    // taken either.
    public bool TryReserve(int inserts, int removes)
    {
        var waitingInserts = _inserts + inserts;
        var waitingRemoves = _removes + removes;
        var room = Room();
        var counted = Need(_height, room, _layout.LeastSeparators, waitingInserts, waitingRemoves);

        // The pool counts its nodes as an Int32: a need above that is a reservation that cannot be made, and
        // it is refused as one the arena cannot hold, before anything is taken.
        if (counted > int.MaxValue)
        {
            return false;
        }

        var need = (int)counted;

        // The managed room first, so that nothing of the arena is taken when it cannot be had; the room of
        // the pool includes the standing reserve, so that a top-up allocates nothing of the managed heap.
        GrowPath(_height + Growth(waitingInserts, room, _layout.LeastSeparators));
        _pool.EnsureCapacity(Math.Max(need, (int)StandingReserve(room)));

        var taken = 0;

        try
        {
            while (_pool.Count < need)
            {
                if (!_arena.TryAllocatePrepared(_layout.PayloadBytes, BlockKindEnum.IndexNode, default, out var node))
                {
                    GiveBack(taken);

                    return false;
                }

                _pool.Push(node);
                taken++;
            }

            if (need > _places)
            {
                _retired.Reserve(need - _places);
                _places = need;
            }
        }
        catch
        {
            GiveBack(taken);
            throw;
        }

        _inserts = waitingInserts;
        _removes = waitingRemoves;
        AssertReserved();

        return true;
    }

    // For the writer, in the hold that prepares a write, once every index of the write has its need
    // (TryReserve): tops the pool up to the standing reserve, as far as the arena gives (see the class).
    // What the arena refuses, or the native allocator cannot supply, is not taken, and nothing fails.
    public void TopUp()
    {
        var reserve = StandingReserve(Room());

        try
        {
            while (_pool.Count < reserve && _arena.TryAllocatePrepared(_layout.PayloadBytes, BlockKindEnum.IndexNode, default, out var node))
            {
                _pool.Push(node);
            }
        }
        catch (OutOfMemoryException)
        {
            // The native allocator could not supply a segment: the rest of the reserve is not had, which
            // is not a failure of the write.
        }

        AssertReserved();
    }

    // For the writer: gives back what TryReserve took for a write that is withdrawn before its
    // publication: every node above what the writes still prepared need goes back to the arena, the
    // standing reserve with them, so that what a write took and did not publish never stays in the pool
    // (see the class).
    public void Release(int inserts, int removes)
    {
        _inserts -= inserts;
        _removes -= removes;
        Trim(keepsReserve: false);
    }

    // For the writer, before a write is refused for memory: gives back to the arena every node of the pool
    // above the need of the writes prepared and not published, the standing reserve with them, so that the
    // reserve is never the reason of a refusal (see the class).
    public void GiveBackReserve() => Trim(keepsReserve: false);

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

    // Room in the writer's path for the most levels the tree can have while the writes reserved wait, and
    // for the nodes one publication replaces, at most one more than the levels: a remove that merges two
    // leaves replaces both and their parent.
    private void GrowPath(int levels)
    {
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

    // After a publication, a withdrawal or before a refusal: the nodes above the need of the writes still
    // waiting go back to the arena, but for the standing reserve after a publication, and the places above
    // the need to the retired list (see the class).
    private void Trim(bool keepsReserve)
    {
        var room = Room();
        var need = NeedOfWaiting(room);
        var kept = keepsReserve ? Math.Max(need, StandingReserve(room)) : need;

        while (_pool.Count > kept)
        {
            _arena.Free(_pool.Pop());
        }

        if (_places > need)
        {
            _retired.Release(_places - (int)need);
            _places = (int)need;
        }

        AssertReserved();
    }

    // For a Debug build, after every change of the pool: the invariant of the reservation, and the most
    // the pool holds (see the class).
    [Conditional("DEBUG")]
    private void AssertReserved()
    {
        var room = Room();
        var need = NeedOfWaiting(room);
        var most = Math.Max(need, StandingReserve(room));

        Debug.Assert(_pool.Count >= need, $"The index {_layout.Index.Name} holds {_pool.Count} nodes for writes that can take {need}.");
        Debug.Assert(_pool.Count <= most, $"The index {_layout.Index.Name} holds {_pool.Count} nodes, above the larger of the need of the writes waiting, {need}, and the standing reserve.");
        Debug.Assert(_places >= need, $"The index {_layout.Index.Name} holds {_places} places in the retired list for writes that can use {need}.");
    }

    // The need of the writes waiting at the tree as it is now, whose root has the room given (see the
    // class).
    private long NeedOfWaiting(int room) => Need(_height, room, _layout.LeastSeparators, _inserts, _removes);

    // The standing reserve at the tree as it is now, whose root has the room given: the need of one change
    // of key, an insert and a remove (see the class).
    private long StandingReserve(int room) => Need(_height, room, _layout.LeastSeparators, inserts: 1, removes: 1);

    // The most nodes the inserts and the removes given, waiting, can take from a tree of the height given
    // whose root has the room given, and the most places in the retired list they can use (see the class,
    // "The reservation"). The k-th insert counts 2 H + 1 at the height H = h + Growth(k - 1) it can find,
    // and every remove H + 1 at the height h + Growth(p) after all of them. A remove never makes the tree
    // higher, nor its root fuller without making it lower by a level, so the order of the writes does not
    // matter. The sum over the inserts in closed form, so that it costs the same for any number of writes
    // waiting: with r the room, L the least separators, p the inserts and q the removes, every division
    // integer and the whole in long,
    //
    //   m = max(0, p - 1 - r), a = m / L, b = m % L, S = m + L a (a - 1) / 2 + a b,
    //   Need = p (2h + 1) + 2 S + q (h + Growth(p) + 1),
    //
    // where S is the sum of Growth(k) for k = 0 to p - 1. Growth(k) is 0 up to k = r, and for the m terms
    // beyond the room it is 1, and on top of that 0 for the first L of them, 1 for the next L, and so on:
    // a whole steps of L, which add L (0 + 1 + ... + (a - 1)), and b terms more, which add a each.
    internal static long Need(int height, int room, int leastSeparators, int inserts, int removes)
    {
        long least = leastSeparators;
        var beyondRoom = Math.Max(0L, inserts - 1L - room);
        var steps = beyondRoom / least;
        var rest = beyondRoom % least;
        var growths = beyondRoom + (least * steps * (steps - 1) / 2) + (steps * rest);

        return (inserts * ((2L * height) + 1)) + (2 * growths) + (removes * (height + Growth(inserts, room, leastSeparators) + 1L));
    }

    // The inserts the root takes before it can split: a node with fewer than LeastSeparators separators,
    // or entries, fits one more. 0 for an empty tree, whose first insert makes a root.
    private int Room() => _root == 0 ? 0 : Math.Max(0, _layout.LeastSeparators - NodeAt(LoadRoot()).Count);

    // The rule of growth: the most levels the inserts given can add to a tree whose root has the room
    // given, the first after the room of the root, and each further one after LeastSeparators more, since
    // the root a growth makes holds one separator.
    private static int Growth(int inserts, int room, int leastSeparators) =>
        inserts <= room ? 0 : 1 + ((inserts - 1 - room) / leastSeparators);

    private void TakeInsert()
    {
        if (_inserts == 0)
        {
            throw new InvalidOperationException($"An insert into the index {_layout.Index.Name} is published that reserved nothing.");
        }

        _inserts--;
    }

    // The end of a publication: the nodes its store replaced are retired, and what is above the larger of
    // the need of the writes still waiting and the standing reserve goes back.
    private void AfterPublication()
    {
        for (var index = 0; index < _replacedCount; index++)
        {
            _retired.RetireNode(_layout, _replaced[index]);
            _places--;
        }

        _replacedCount = 0;
        Trim(keepsReserve: true);
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

    // The bytes the entries of the leaf take with their offsets: those of the leaf it copies, with the one
    // change.
    private int BytesOf(scoped in EditedLeaf edited) => edited.Bytes(OffsetBytes);

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

        edited.CopyTo(ref writer, first, end);
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
            writer.AddRange(neighbour, 0, neighbour.Count);
        }

        edited.CopyTo(ref writer, 0, edited.Count);

        if (neighbourSlot > slot)
        {
            writer.AddRange(neighbour, 0, neighbour.Count);
        }

        Complete(ref writer, merged);
        Replaced(leafAddress);
        Replaced(neighbourAddress);

        // The second of the two children goes away, and the merged node takes the place of the first.
        WithoutChild(level - 1, Math.Max(slot, neighbourSlot), merged);

        return true;
    }

    // The child that the path took in the inner node of the level goes away: a node left without
    // children goes away from its own parent, and a tree left without a node has none; otherwise the
    // node loses the child and a separator beside it (WithoutChild).
    private void RemoveChild(int level)
    {
        var parentAddress = _path[level];

        if (NodeAt(parentAddress).Count > 0)
        {
            WithoutChild(level, _slots[level], merged: default);

            return;
        }

        Replaced(parentAddress);

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

    // The inner node of the level, which has a separator at least, loses the child at the place given and
    // the separator before it, or after it for the first child: it is copied without the two, and with
    // the merged node in place of the child before it, when two children were merged (TryMergeLeaf); a
    // root left with one child gives way to that child.
    private void WithoutChild(int level, int gone, ArenaAddress merged)
    {
        var parentAddress = _path[level];
        var parent = NodeAt(parentAddress);
        var separator = gone > 0 ? gone - 1 : 0;
        // The child beside the one that goes, at which the merged node goes after a merge, and the child a
        // root left with one child keeps. In a removal kept is that child itself, written over itself.
        var beside = gone > 0 ? gone - 1 : 1;
        var kept = merged.IsNull ? parent.ChildAt(beside) : merged;

        Replaced(parentAddress);

        if (parent.Count == 1 && level == 0)
        {
            StoreRoot(kept);
            _height--;

            return;
        }

        var copy = TakeNode();
        var writer = new NodeWriter(_layout, PayloadOf(copy), parent.Level, parent.Count - 1);

        for (var place = 0; place < parent.Count; place++)
        {
            if (place != separator)
            {
                writer.Add(parent.EntryAt(place));
            }
        }

        for (var child = 0; child <= parent.Count; child++)
        {
            if (child == beside)
            {
                writer.AddChild(kept);
            }
            else if (child != gone)
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

    // A node of the reservation, live from now on; nothing reaches it before the store that publishes it.
    private ArenaAddress TakeNode()
    {
        if (!_pool.TryPop(out var node))
        {
            throw new InvalidOperationException($"A publication of the index {_layout.Index.Name} took more nodes than were reserved for it.");
        }

        _arena.Publish(node);

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

        Span<ValueDescriptor> texts = stackalloc ValueDescriptor[_layout.TextParts];
        var count = _layout.SharedTexts(entry, texts);

        for (var text = 0; text < count; text++)
        {
            _arena.Texts.Publish(ValueBlocks.AddressOf(texts[text]));
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

    // The child of an inner node whose range holds the entry: the number of separators at or below it. For
    // the writer.
    private int ChildOf(scoped in IndexNode node, ReadOnlySpan<byte> entry) => EntriesBelow(node, entry, equalBelow: true);

    // The place of the first entry of a leaf at or after the entry: the number of entries below it. For the
    // writer.
    private int FirstAtOrAfter(scoped in IndexNode leaf, ReadOnlySpan<byte> entry) => EntriesBelow(leaf, entry, equalBelow: false);

    // The binary search over the entries or the separators of a node by a search key: how many lie below
    // the probe, with those equal to it counted below when equalBelow is true. It and the search by an
    // entry below are one search written for each kind of probe, since a body shared between them would
    // need an interface, a generic or a delegate.
    private int EntriesBelow(scoped in IndexNode node, scoped in SearchKey probe, bool equalBelow)
    {
        // An entry counts below the probe when the order of the probe against it is at least this: 1, the
        // probe after the entry, or 0 when an entry equal to the probe counts below as well. A threshold, so
        // that each probe of the search tests the order once (JIT listing at full optimization).
        var least = equalBelow ? 0 : 1;
        var low = 0;
        var high = node.Count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.Compare(probe, node.EntryAt(middle)) >= least)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    // The same search by an entry.
    private int EntriesBelow(scoped in IndexNode node, ReadOnlySpan<byte> entry, bool equalBelow)
    {
        var least = equalBelow ? 0 : 1;
        var low = 0;
        var high = node.Count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.Compare(entry, node.EntryAt(middle)) >= least)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
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

        // The bytes the entries take, each with the offset bytes given: those of the leaf, with the entry
        // added, replaced or taken away.
        public int Bytes(int offsetBytes)
        {
            var bytes = _leaf.UsedBytes;

            return _change switch
            {
                Inserts => bytes + _entry.Length + offsetBytes,
                Replaces => bytes + _entry.Length - _leaf.EntryAt(_place).Length,
                _ => bytes - _leaf.EntryAt(_place).Length - offsetBytes,
            };
        }

        // Writes the entries from first to end as the next entries of the writer: the entries of the leaf
        // before the place of the change as one block, the entry of the change, and the entries of the leaf
        // after it as one block (NodeWriter.AddRange).
        public void CopyTo(ref NodeWriter writer, int first, int end)
        {
            var before = Math.Min(end, _place);

            if (first < before)
            {
                writer.AddRange(_leaf, first, before);
            }

            if (_change != Removes && first <= _place && _place < end)
            {
                writer.Add(_entry);
            }

            // After the change, the entry at a place of the edited leaf is the leaf's one place before it
            // for an insert, at it for a replace, and one place after it for a remove.
            var after = Math.Max(first, _change == Removes ? _place : _place + 1);

            if (after < end)
            {
                writer.AddRange(_leaf, after - _change, end - _change);
            }
        }
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
