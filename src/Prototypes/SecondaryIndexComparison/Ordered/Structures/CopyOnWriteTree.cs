using System.Runtime.CompilerServices;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

// Candidate A: a B+tree whose published nodes never change, except one child address of an inner
// node, which the writer replaces by one store of eight bytes with release semantics.
//
// Modelled on, not adapted from (no code is ported):
// - LMDB (https://github.com/LMDB/lmdb, OpenLDAP Public License 2.8, commit
//   74dd6c3ec648c31857fe0a100f11c2a34619e2d7, libraries/liblmdb/mdb.c): copy-on-write of a page before
//   it changes (mdb_page_touch), a split biased to keep the left page full when the new entry goes
//   on the last place (mdb_page_split), a merge below a quarter of fill (mdb_rebalance), and pages
//   freed only after the oldest reader that could see them has left (mdb_find_oldest). LMDB copies
//   the whole path to the root, because its commit point is one meta page; here an aligned store of
//   one address is atomic, so only the nodes whose content changes are copied, and the store into the
//   nearest unchanged ancestor (or the root) is the publication.
// - The principle of ROWEX (Leis, Scheibner, Kemper, Neumann, "The ART of Practical Synchronization",
//   DaMoN 2016, section 4): readers take no lock and check no version; a node that changes is built
//   privately and published by one atomic store of its address; the replaced node is retired to an
//   epoch, and a reader may still read it.
// - The walk from leaf to leaf of LeanStore (https://github.com/leanstore/leanstore, MIT, commit
//   fcb64bf39cf69c435173f76e062cbb305a9a998f, backend/leanstore/storage/btree/core/
//   BTreeGenericIterator.hpp): no sibling links; the next leaf is found by a new descent at the fence
//   of the leaf, the same way forwards and backwards.
//
// Readers. A read enters the protection, loads the root and every child address with acquire
// semantics, reads the node behind it with plain loads, and leaves. A node it reaches was written
// whole before the store that published its address, and is never written again until it is freed,
// which is after the read has left; so a read sees each node as one state, never retries, and never
// reads memory that was freed. Two nodes of one read may belong to different moments: the tree it
// walks is the one the writer published at some moment for each node, which is all a step needs, as
// every node holds entries in order and every separator bounds its children.
//
// The writer. Prepare takes from the arena, before the publication, as many nodes as the write can
// need (a split on every level and a new root), and places in the list of retired blocks; the
// publication takes nodes from that pool, publishes and retires, and allocates nothing.
internal sealed unsafe class CopyOnWriteTree<TEntry, TProbe, TOrder> : IOrderedIndex<TEntry, TProbe>
    where TEntry : unmanaged
    where TProbe : unmanaged
    where TOrder : struct, IEntryOrder<TEntry, TProbe>
{
    private const int MostLevels = 32;

    private readonly NodeArena _arena;

    private readonly ReadEpochs _epochs;

    private readonly RetiredBlocks _retired;

    private readonly TOrder _order;

    private readonly NodeShape _shape;

    // Nodes taken before the publications that will use them, prepared in the arena.
    private readonly Stack<ulong> _pool = new();

    // The path of the writer's last descent: the node of each level and the child it took.
    private readonly ulong[] _path = new ulong[MostLevels];

    private readonly int[] _slots = new int[MostLevels];

    // Blocks a publication replaced, retired once its store is done.
    private readonly ulong[] _replaced = new ulong[MostLevels * 4];

    // Scratch of the writer for the split of an inner node: one separator and one child more than it holds.
    private readonly TEntry[] _separators;

    private readonly ulong[] _children;

    private ulong _root;

    private int _height = 1;

    private long _count;

    private long _nodes;

    private int _preparedWrites;

    private int _replacedCount;

    public CopyOnWriteTree(NodeArena arena, ReadEpochs epochs, TOrder order, int nodeBytes)
    {
        _arena = arena;
        _epochs = epochs;
        _retired = new RetiredBlocks(epochs, arena);
        _order = order;
        _shape = new NodeShape(nodeBytes, sizeof(TEntry));
        _separators = new TEntry[_shape.InnerCapacity + 1];
        _children = new ulong[_shape.InnerCapacity + 2];

        Prepare(1);
        _root = TakeNode(leaf: true);
        _preparedWrites--;
    }

    public string Name => $"CopyOnWriteTree/{_shape.BlockBytes}";

    public long Count => _count;

    public StructureBytes Bytes => new(_nodes * _shape.BlockBytes, 0, _nodes, _height);

    // The most nodes one write can take: a split on every level, one level more than now for a write
    // of a batch that grew the tree, and a new root.
    private int NodesPerWrite => (2 * (_height + 2)) + 1;

    public void Prepare(int writes)
    {
        _preparedWrites += writes;

        var needed = _preparedWrites * NodesPerWrite;

        while (_pool.Count < needed)
        {
            _pool.Push(_arena.Allocate(_shape.PayloadBytes, prepared: true));
        }

        _retired.Reserve(needed);
    }

    public void Withdraw(int writes)
    {
        _preparedWrites -= writes;

        // What no publication will use goes back at once: no reader ever reached it.
        while (_pool.Count > _preparedWrites * NodesPerWrite)
        {
            _arena.Free(_pool.Pop(), BlockStateEnum.Prepared);
        }
    }

    public void AfterPublication() => _retired.AfterWrite();

    public void DrainAll() => _retired.Drain();

    public void Dispose()
    {
        // The arena belongs to the scenario, which disposes it with every node.
    }

    public int Read(in TProbe from, bool inclusive, bool forward, bool keyOnly, Span<TEntry> into)
    {
        var slot = _epochs.Enter();

        try
        {
            return ReadInside(from, inclusive, forward, keyOnly, into);
        }
        finally
        {
            _epochs.Exit(slot);
        }
    }

    public void Insert(in TEntry entry)
    {
        TakeWrite();
        Descend(entry);

        var level = _height - 1;
        var leafAddress = _path[level];
        var leaf = _arena.Payload(leafAddress);
        var count = CountOf(leaf);
        var position = LowerBound(leaf, count, entry);

        if (position < count && _order.Compare(entry, At(leaf, position)) == 0)
        {
            throw new InvalidOperationException("The index holds this key with this identity already.");
        }

        if (count < _shape.LeafCapacity)
        {
            var copy = TakeNode(leaf: true);
            var target = _arena.Payload(copy);

            CopyEntries(leaf, 0, target, 0, position);
            At(target, position) = entry;
            CopyEntries(leaf, position, target, position + 1, count - position);
            CountOf(target) = count + 1;

            Replace(level, copy);
            Replaced(leafAddress);
        }
        else
        {
            SplitLeaf(leafAddress, leaf, count, position, entry);
        }

        RetireReplaced();
        _count++;
    }

    public bool Remove(in TEntry entry)
    {
        TakeWrite();
        Descend(entry);

        var level = _height - 1;
        var leafAddress = _path[level];
        var leaf = _arena.Payload(leafAddress);
        var count = CountOf(leaf);
        var position = LowerBound(leaf, count, entry);

        if (position == count || _order.Compare(entry, At(leaf, position)) != 0)
        {
            return false;
        }

        if (count == 1 && level > 0)
        {
            RemoveChild(level - 1);
            Replaced(leafAddress);
        }
        else if (count - 1 < _shape.LeafCapacity / 4 && level > 0 && TryMergeLeaf(leafAddress, leaf, count, position))
        {
            // TryMergeLeaf published the merged leaf.
        }
        else
        {
            var copy = TakeNode(leaf: true);
            var target = _arena.Payload(copy);

            CopyEntries(leaf, 0, target, 0, position);
            CopyEntries(leaf, position + 1, target, position, count - position - 1);
            CountOf(target) = count - 1;

            Replace(level, copy);
            Replaced(leafAddress);
        }

        RetireReplaced();
        _count--;

        return true;
    }

    public List<TEntry> Validate()
    {
        var entries = new List<TEntry>((int)_count);
        var nodes = 0L;

        ValidateNode(_root, 0, hasLower: false, default, hasUpper: false, default, entries, ref nodes);

        if (entries.Count != _count || nodes != _nodes)
        {
            throw new InvalidOperationException($"The tree counts {_count} entries and {_nodes} nodes; it holds {entries.Count} and {nodes}.");
        }

        return entries;
    }

    // One step of a read (see the class): a descent to the leaf of the probe, then the entries in the
    // direction asked; at the end of the leaf, a new descent at its fence, the separator that bounds it
    // in that direction, until the span is full, the key ends or the index does.
    private int ReadInside(TProbe from, bool inclusive, bool forward, bool keyOnly, Span<TEntry> into)
    {
        var target = from;
        var targetInclusive = inclusive;
        var copied = 0;

        while (copied < into.Length)
        {
            var node = _arena.Payload(Volatile.Read(ref _root));

            // The fence stays where it is in its node, which no write changes while the read is
            // inside: a probe made from it may point into the entry (form 2 of a text key).
            TEntry* fence = null;

            while (!IsLeaf(node))
            {
                var count = CountOf(node);
                var child = forward || targetInclusive ? FirstAbove(node, count, target) : FirstNotBelow(node, count, target);

                if (forward && child < count)
                {
                    fence = (TEntry*)Unsafe.AsPointer(ref At(node, child));
                }
                else if (!forward && child > 0)
                {
                    fence = (TEntry*)Unsafe.AsPointer(ref At(node, child - 1));
                }

                node = _arena.Payload(Volatile.Read(ref Children(node)[child]));
            }

            var entries = CountOf(node);

            if (forward)
            {
                for (var index = targetInclusive ? FirstNotBelow(node, entries, target) : FirstAbove(node, entries, target); index < entries && copied < into.Length; index++)
                {
                    ref var entry = ref At(node, index);

                    if (keyOnly && !_order.SameKey(from, entry))
                    {
                        return copied;
                    }

                    into[copied++] = entry;
                }
            }
            else
            {
                for (var index = (targetInclusive ? FirstAbove(node, entries, target) : FirstNotBelow(node, entries, target)) - 1; index >= 0 && copied < into.Length; index--)
                {
                    ref var entry = ref At(node, index);

                    if (keyOnly && !_order.SameKey(from, entry))
                    {
                        return copied;
                    }

                    into[copied++] = entry;
                }
            }

            if (fence == null)
            {
                break;
            }

            // Forwards every entry of the next leaf is at or above the fence; backwards every entry of
            // the leaf before is below it.
            target = _order.ProbeOf(*fence);
            targetInclusive = forward;
        }

        return copied;
    }

    // The first place whose entry is above the probe: the number of entries at or below it.
    private int FirstAbove(byte* node, int count, in TProbe probe)
    {
        var low = 0;
        var high = count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.CompareProbe(probe, At(node, middle)) < 0)
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

    // The first place whose entry is at or above the probe: the number of entries below it.
    private int FirstNotBelow(byte* node, int count, in TProbe probe)
    {
        var low = 0;
        var high = count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.CompareProbe(probe, At(node, middle)) <= 0)
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

    // For the writer: the first place whose entry is at or above the entry.
    private int LowerBound(byte* node, int count, in TEntry entry)
    {
        var low = 0;
        var high = count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.Compare(entry, At(node, middle)) <= 0)
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

    // For the writer: the child of an inner node whose range holds the entry.
    private int ChildFor(byte* node, int count, in TEntry entry)
    {
        var low = 0;
        var high = count;

        while (low < high)
        {
            var middle = (low + high) >>> 1;

            if (_order.Compare(entry, At(node, middle)) < 0)
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

    private void Descend(in TEntry entry)
    {
        var address = _root;

        for (var level = 0; ; level++)
        {
            _path[level] = address;

            var node = _arena.Payload(address);

            if (IsLeaf(node))
            {
                return;
            }

            var slot = ChildFor(node, CountOf(node), entry);

            _slots[level] = slot;
            address = Children(node)[slot];
        }
    }

    // Whether every node above this level on the path took its last child: the node of this level is
    // the last of its level.
    private bool IsRightmost(int level)
    {
        for (var above = 0; above < level; above++)
        {
            if (_slots[above] != CountOf(_arena.Payload(_path[above])))
            {
                return false;
            }
        }

        return true;
    }

    private void SplitLeaf(ulong leafAddress, byte* leaf, int count, int position, in TEntry entry)
    {
        var level = _height - 1;
        ulong left;
        ulong right;

        if (position == count && IsRightmost(level))
        {
            // An append at the end of the index: the full leaf stays as it is and a new leaf takes the
            // entry, so ascending inserts leave full leaves (LMDB biases its split the same way).
            left = leafAddress;
            right = TakeNode(leaf: true);

            var target = _arena.Payload(right);

            At(target, 0) = entry;
            CountOf(target) = 1;
        }
        else
        {
            left = TakeNode(leaf: true);
            right = TakeNode(leaf: true);

            var leftNode = _arena.Payload(left);
            var rightNode = _arena.Payload(right);
            var total = count + 1;
            var leftCount = total / 2;

            for (var index = 0; index < total; index++)
            {
                var value = index < position ? At(leaf, index) : index == position ? entry : At(leaf, index - 1);

                if (index < leftCount)
                {
                    At(leftNode, index) = value;
                }
                else
                {
                    At(rightNode, index - leftCount) = value;
                }
            }

            CountOf(leftNode) = leftCount;
            CountOf(rightNode) = total - leftCount;
            Replaced(leafAddress);
        }

        InsertIntoParent(level - 1, left, right, At(_arena.Payload(right), 0), leftChanged: left != leafAddress);
    }

    // The child at the path's slot of this level was split into left and right, divided by the
    // separator: the inner node of this level gets one separator and one child more, by a copy, or is
    // split itself.
    private void InsertIntoParent(int level, ulong left, ulong right, TEntry separator, bool leftChanged)
    {
        if (level < 0)
        {
            var root = TakeNode(leaf: false);
            var node = _arena.Payload(root);

            CountOf(node) = 1;
            At(node, 0) = separator;
            Children(node)[0] = left;
            Children(node)[1] = right;

            Volatile.Write(ref _root, root);
            _height++;

            return;
        }

        var parentAddress = _path[level];
        var parent = _arena.Payload(parentAddress);
        var count = CountOf(parent);
        var slot = _slots[level];

        if (count == _shape.InnerCapacity && slot == count && !leftChanged && IsRightmost(level))
        {
            // An append at the end of the index on a full inner node: it stays as it is, and a new node
            // with the new child alone goes up beside it.
            var alone = TakeNode(leaf: false);

            CountOf(_arena.Payload(alone)) = 0;
            Children(_arena.Payload(alone))[0] = right;
            InsertIntoParent(level - 1, parentAddress, alone, separator, leftChanged: false);

            return;
        }

        // The separators and children with the new ones, in the writer's scratch.
        var separators = _separators.AsSpan(0, count + 1);
        var children = _children.AsSpan(0, count + 2);
        var parentChildren = Children(parent);

        for (var index = 0; index <= count; index++)
        {
            if (index < slot)
            {
                separators[index] = At(parent, index);
            }
            else if (index == slot)
            {
                separators[index] = separator;
            }
            else
            {
                separators[index] = At(parent, index - 1);
            }
        }

        for (var index = 0; index <= count + 1; index++)
        {
            children[index] = index < slot ? parentChildren[index] : index == slot ? left : index == slot + 1 ? right : parentChildren[index - 1];
        }

        Replaced(parentAddress);

        if (count < _shape.InnerCapacity)
        {
            var copy = TakeNode(leaf: false);

            WriteInner(_arena.Payload(copy), separators, children);
            Replace(level, copy);

            return;
        }

        // Split: the middle separator goes up.
        var middle = (count + 1) / 2;
        var newLeft = TakeNode(leaf: false);
        var newRight = TakeNode(leaf: false);

        WriteInner(_arena.Payload(newLeft), separators[..middle], children[..(middle + 1)]);
        WriteInner(_arena.Payload(newRight), separators[(middle + 1)..], children[(middle + 1)..]);
        InsertIntoParent(level - 1, newLeft, newRight, separators[middle], leftChanged: true);
    }

    // A leaf with too few entries after the delete is merged with a neighbour under the same parent,
    // when both fit three quarters of a leaf; false, with nothing changed, when there is no such
    // neighbour.
    private bool TryMergeLeaf(ulong leafAddress, byte* leaf, int count, int position)
    {
        var level = _height - 1;
        var parent = _arena.Payload(_path[level - 1]);
        var slot = _slots[level - 1];
        var separators = CountOf(parent);
        var neighbourSlot = slot > 0 ? slot - 1 : slot < separators ? slot + 1 : -1;

        if (neighbourSlot < 0)
        {
            return false;
        }

        var neighbourAddress = Children(parent)[neighbourSlot];
        var neighbour = _arena.Payload(neighbourAddress);
        var neighbourCount = CountOf(neighbour);

        if (neighbourCount + count - 1 > _shape.LeafCapacity * 3 / 4)
        {
            return false;
        }

        var merged = TakeNode(leaf: true);
        var target = _arena.Payload(merged);
        var written = 0;

        if (neighbourSlot < slot)
        {
            CopyEntries(neighbour, 0, target, 0, neighbourCount);
            written = neighbourCount;
        }

        CopyEntries(leaf, 0, target, written, position);
        CopyEntries(leaf, position + 1, target, written + position, count - position - 1);
        written += count - 1;

        if (neighbourSlot > slot)
        {
            CopyEntries(neighbour, 0, target, written, neighbourCount);
            written += neighbourCount;
        }

        CountOf(target) = written;
        Replaced(leafAddress);
        Replaced(neighbourAddress);
        ReplaceChildPair(level - 1, Math.Min(slot, neighbourSlot), merged);

        return true;
    }

    // Children first and first + 1 of the inner node of this level become one, the merged node: the
    // node is copied without the separator between them; a root left with one child gives way to it.
    private void ReplaceChildPair(int level, int first, ulong merged)
    {
        var parentAddress = _path[level];
        var parent = _arena.Payload(parentAddress);
        var count = CountOf(parent);

        Replaced(parentAddress);

        if (count == 1 && level == 0)
        {
            Volatile.Write(ref _root, merged);
            _height--;

            return;
        }

        var separators = _separators.AsSpan(0, count - 1);
        var children = _children.AsSpan(0, count);
        var parentChildren = Children(parent);

        for (var index = 0; index < count - 1; index++)
        {
            separators[index] = At(parent, index < first ? index : index + 1);
        }

        for (var index = 0; index < count; index++)
        {
            children[index] = index < first ? parentChildren[index] : index == first ? merged : parentChildren[index + 1];
        }

        var copy = TakeNode(leaf: false);

        WriteInner(_arena.Payload(copy), separators, children);
        Replace(level, copy);
    }

    // The child at the path's slot of this level goes away: the node is copied without it and without
    // one separator next to it; a node left without children goes away from its own parent, and a root
    // left with one child gives way to it.
    private void RemoveChild(int level)
    {
        var parentAddress = _path[level];
        var parent = _arena.Payload(parentAddress);
        var count = CountOf(parent);
        var slot = _slots[level];

        Replaced(parentAddress);

        if (count == 0)
        {
            if (level == 0)
            {
                var empty = TakeNode(leaf: true);

                Volatile.Write(ref _root, empty);
                _height = 1;
            }
            else
            {
                RemoveChild(level - 1);
            }

            return;
        }

        var parentChildren = Children(parent);

        if (count == 1 && level == 0)
        {
            Volatile.Write(ref _root, parentChildren[1 - slot]);
            _height--;

            return;
        }

        var removedSeparator = slot > 0 ? slot - 1 : 0;
        var separators = _separators.AsSpan(0, count - 1);
        var children = _children.AsSpan(0, count);

        for (var index = 0; index < count - 1; index++)
        {
            separators[index] = At(parent, index < removedSeparator ? index : index + 1);
        }

        for (var index = 0; index < count; index++)
        {
            children[index] = parentChildren[index < slot ? index : index + 1];
        }

        var copy = TakeNode(leaf: false);

        WriteInner(_arena.Payload(copy), separators, children);
        Replace(level, copy);
    }

    private void WriteInner(byte* node, ReadOnlySpan<TEntry> separators, ReadOnlySpan<ulong> children)
    {
        CountOf(node) = separators.Length;

        for (var index = 0; index < separators.Length; index++)
        {
            At(node, index) = separators[index];
        }

        children.CopyTo(new Span<ulong>(Children(node), children.Length));
    }

    // The publication: the new node of this level takes the place of the old one, by one store with
    // release semantics into the live parent, or into the root.
    private void Replace(int level, ulong address)
    {
        if (level == 0)
        {
            Volatile.Write(ref _root, address);
        }
        else
        {
            Volatile.Write(ref Children(_arena.Payload(_path[level - 1]))[_slots[level - 1]], address);
        }
    }

    private void Replaced(ulong address) => _replaced[_replacedCount++] = address;

    // After the store that published: what it replaced is retired, and no reader that enters from now
    // on can reach it.
    private void RetireReplaced()
    {
        for (var index = 0; index < _replacedCount; index++)
        {
            _retired.Retire(_replaced[index]);
            _nodes--;
        }

        _replacedCount = 0;
    }

    private void TakeWrite()
    {
        if (_preparedWrites == 0)
        {
            throw new InvalidOperationException("A publication needs a prepared write.");
        }

        _preparedWrites--;
    }

    private ulong TakeNode(bool leaf)
    {
        if (!_pool.TryPop(out var address))
        {
            throw new InvalidOperationException("A publication took more nodes than its write prepared.");
        }

        _arena.Publish(address);
        _nodes++;

        var node = _arena.Payload(address);

        CountOf(node) = 0;
        *(int*)(node + 4) = leaf ? 1 : 0;

        return address;
    }

    private void ValidateNode(ulong address, int level, bool hasLower, TEntry lower, bool hasUpper, TEntry upper, List<TEntry> entries, ref long nodes)
    {
        nodes++;

        var node = _arena.Payload(address);
        var count = CountOf(node);

        if (IsLeaf(node))
        {
            if (level != _height - 1 || count > _shape.LeafCapacity || (count == 0 && level > 0))
            {
                throw new InvalidOperationException($"A leaf at level {level} of {_height} holds {count} entries.");
            }

            for (var index = 0; index < count; index++)
            {
                var entry = At(node, index);

                if ((index > 0 && _order.Compare(At(node, index - 1), entry) >= 0)
                    || (hasLower && _order.Compare(entry, lower) < 0)
                    || (hasUpper && _order.Compare(entry, upper) >= 0))
                {
                    throw new InvalidOperationException("A leaf is out of order or outside its separators.");
                }

                entries.Add(entry);
            }

            return;
        }

        if (count > _shape.InnerCapacity || level >= _height - 1)
        {
            throw new InvalidOperationException($"An inner node at level {level} of {_height} holds {count} separators.");
        }

        for (var child = 0; child <= count; child++)
        {
            var childLower = child > 0 ? At(node, child - 1) : lower;
            var childUpper = child < count ? At(node, child) : upper;

            ValidateNode(Children(node)[child], level + 1, child > 0 || hasLower, childLower, child < count || hasUpper, childUpper, entries, ref nodes);
        }
    }

    private void CopyEntries(byte* from, int fromIndex, byte* to, int toIndex, int count) =>
        Buffer.MemoryCopy(&((TEntry*)(from + NodeShape.HeaderBytes))[fromIndex], &((TEntry*)(to + NodeShape.HeaderBytes))[toIndex], count * sizeof(TEntry), count * sizeof(TEntry));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref int CountOf(byte* node) => ref *(int*)node;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLeaf(byte* node) => *(int*)(node + 4) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref TEntry At(byte* node, int index) => ref ((TEntry*)(node + NodeShape.HeaderBytes))[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong* Children(byte* node) => (ulong*)(node + _shape.ChildrenOffset);
}
