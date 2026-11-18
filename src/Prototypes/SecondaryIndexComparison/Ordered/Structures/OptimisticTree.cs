using System.Runtime.CompilerServices;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

// Candidate B: a B+tree that the writer changes in place, each node with a version that readers
// validate, after optimistic lock coupling.
//
// Modelled on, not adapted from (no code is ported):
// - LeanStore (https://github.com/leanstore/leanstore, MIT, commit
//   fcb64bf39cf69c435173f76e062cbb305a9a998f): the optimistic latch of
//   backend/leanstore/sync-primitives/Latch.hpp (a version word, a writer that makes it odd while it
//   changes the node and even again after, a reader that reads the version, reads the node and checks
//   the version again, restarting when it changed), the in-place insert and delete of
//   backend/leanstore/storage/btree/core/BTreeNode.cpp, the walk by a new descent at the fence
//   (BTreeGenericIterator.hpp). LeanStore takes the latch of a leaf shared for a scan; here a scan is
//   optimistic as well, because readers take no lock.
// - Optimistic lock coupling as ARTSynchronized implements it (https://github.com/flode/ARTSynchronized,
//   Apache-2.0, commit f00003ec7991da09c3dfc1c84da97c1965106eb3, OptimisticLockCoupling/Tree.cpp): read
//   the child address, check the parent, read the child's version, check the parent again; a node
//   taken out of the tree is made obsolete and retired to an epoch.
//
// One writer, so no latch is taken: the writer makes the version of every node it changes odd before
// the change and even after it. Its first store is an interlocked increment, a full fence, so no store
// of the change becomes visible before the odd version; its last is a store with release semantics, so
// none becomes visible after the even one. A reader loads the version with acquire semantics, reads
// the node with plain loads, and validates: a load barrier (Volatile.ReadBarrier), so that the plain
// loads are done before the version is loaded again, and the version unchanged. It validates the parent
// before it follows a child address, so it never follows an address it read from a node in the middle
// of a change; the addresses are moved a word at a time, so a load of one is never torn. What it read
// between two validations may be torn when the version changed, and is then thrown away: for that
// reason this candidate holds fixed keys only. An entry that names text would be dereferenced before
// the validation could tell that it is torn, so every comparison would need a validation of its own.
internal sealed unsafe class OptimisticTree<TEntry, TOrder> : IOrderedIndex<TEntry, TEntry>
    where TEntry : unmanaged
    where TOrder : struct, IEntryOrder<TEntry, TEntry>
{
    private const int MostLevels = 32;

    // A reader's restarts, on its own thread: what a measurement reports, never a decision.
    [ThreadStatic]
    private static long t_restarts;

    private readonly NodeArena _arena;

    private readonly ReadEpochs _epochs;

    private readonly RetiredBlocks _retired;

    private readonly TOrder _order;

    private readonly NodeShape _shape;

    private readonly Stack<ulong> _pool = new();

    private readonly ulong[] _path = new ulong[MostLevels];

    private readonly int[] _slots = new int[MostLevels];

    // Nodes whose version a publication made odd, made even again at its end.
    private readonly ulong[] _changing = new ulong[MostLevels * 4];

    // Nodes a publication took out of the tree, retired at its end.
    private readonly ulong[] _removed = new ulong[MostLevels * 4];

    private readonly TEntry[] _separators;

    private readonly ulong[] _children;

    private ulong _root;

    private int _height = 1;

    private long _count;

    private long _nodes;

    private int _preparedWrites;

    private int _changingCount;

    private int _removedCount;

    public OptimisticTree(NodeArena arena, ReadEpochs epochs, TOrder order, int nodeBytes)
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

    // The restarts of the reads made on the calling thread.
    public static long RestartsOnThisThread => t_restarts;

    public string Name => $"OptimisticTree/{_shape.BlockBytes}";

    public long Count => _count;

    public StructureBytes Bytes => new(_nodes * _shape.BlockBytes, 0, _nodes, _height);

    // A split on every level and a new root.
    private int NodesPerWrite => _height + 3;

    public void Prepare(int writes)
    {
        _preparedWrites += writes;

        var needed = _preparedWrites * NodesPerWrite;

        while (_pool.Count < needed)
        {
            _pool.Push(_arena.Allocate(_shape.PayloadBytes, prepared: true));
        }

        _retired.Reserve(_preparedWrites * (MostLevels + 1));
    }

    public void Withdraw(int writes)
    {
        _preparedWrites -= writes;

        while (_pool.Count > _preparedWrites * NodesPerWrite)
        {
            _arena.Free(_pool.Pop(), BlockStateEnum.Prepared);
        }
    }

    public void AfterPublication() => _retired.AfterWrite();

    public void DrainAll() => _retired.Drain();

    public void Dispose()
    {
        // The arena belongs to the scenario.
    }

    public int Read(in TEntry from, bool inclusive, bool forward, bool keyOnly, Span<TEntry> into)
    {
        var slot = _epochs.Enter();

        try
        {
            var target = from;
            var targetInclusive = inclusive;
            var copied = 0;

            while (copied < into.Length)
            {
                if (!TryReadLeaf(from, ref target, ref targetInclusive, forward, keyOnly, into, ref copied, out var more))
                {
                    t_restarts++;

                    continue;
                }

                if (!more)
                {
                    break;
                }
            }

            return copied;
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
            BeginChange(leafAddress);
            ShiftEntries(leaf, position, count, 1);
            At(leaf, position) = entry;
            CountOf(leaf) = count + 1;
        }
        else
        {
            // Every node from the first ancestor with room down to the leaf changes; all are made odd
            // first, top down, so a reader that passed one of them before the change fails at it.
            var top = _height - 2;

            while (top >= 0 && CountOf(_arena.Payload(_path[top])) == _shape.InnerCapacity)
            {
                top--;
            }

            for (var changing = Math.Max(top, 0); changing <= level; changing++)
            {
                BeginChange(_path[changing]);
            }

            SplitLeaf(leafAddress, leaf, count, position, entry);
        }

        EndPublication();
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
            for (var changing = 0; changing <= level; changing++)
            {
                BeginChange(_path[changing]);
            }

            RemoveChild(level - 1);
            Removed(leafAddress);
        }
        else if (count - 1 < _shape.LeafCapacity / 4 && level > 0 && TryMergeLeaf(leaf, count, position))
        {
            // TryMergeLeaf changed the nodes.
        }
        else
        {
            BeginChange(leafAddress);
            ShiftEntries(leaf, position + 1, count, -1);
            CountOf(leaf) = count - 1;
        }

        EndPublication();
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

    // One leaf of a step: a descent by optimistic lock coupling, then the entries of the leaf, kept
    // only when the leaf validates. False when a validation failed: the step starts this leaf again,
    // with what it copied before. True with more when the walk goes on at the fence of the leaf.
    private bool TryReadLeaf(in TEntry key, ref TEntry target, ref bool targetInclusive, bool forward, bool keyOnly, Span<TEntry> into, ref int copied, out bool more)
    {
        more = false;

        var address = Volatile.Read(ref _root);
        var node = _arena.Payload(address);
        var version = StableVersion(node);

        // A root that a split or a collapse replaced is changed or obsolete; the new root is
        // published before its version is even again.
        if (Volatile.Read(ref _root) != address)
        {
            return false;
        }

        var hasFence = false;
        var fence = default(TEntry);

        while (!IsLeaf(node))
        {
            var count = CountOf(node);
            var child = forward || targetInclusive ? FirstAbove(node, count, target) : FirstNotBelow(node, count, target);

            if (forward && child < count)
            {
                fence = At(node, child);
                hasFence = true;
            }
            else if (!forward && child > 0)
            {
                fence = At(node, child - 1);
                hasFence = true;
            }

            var childAddress = Volatile.Read(ref Children(node)[child]);

            if (!IsValid(node, version))
            {
                return false;
            }

            var childNode = _arena.Payload(childAddress);
            var childVersion = StableVersion(childNode);

            if (!IsValid(node, version))
            {
                return false;
            }

            node = childNode;
            version = childVersion;
        }

        var entries = CountOf(node);
        var start = copied;
        var keyEnded = false;

        if (forward)
        {
            for (var index = targetInclusive ? FirstNotBelow(node, entries, target) : FirstAbove(node, entries, target); index < entries && copied < into.Length; index++)
            {
                var entry = At(node, index);

                if (keyOnly && !_order.SameKey(key, entry))
                {
                    keyEnded = true;

                    break;
                }

                into[copied++] = entry;
            }
        }
        else
        {
            for (var index = (targetInclusive ? FirstAbove(node, entries, target) : FirstNotBelow(node, entries, target)) - 1; index >= 0 && copied < into.Length; index--)
            {
                var entry = At(node, index);

                if (keyOnly && !_order.SameKey(key, entry))
                {
                    keyEnded = true;

                    break;
                }

                into[copied++] = entry;
            }
        }

        if (!IsValid(node, version))
        {
            copied = start;

            return false;
        }

        if (keyEnded || !hasFence)
        {
            return true;
        }

        target = fence;
        targetInclusive = forward;
        more = true;

        return true;
    }

    // The version of a node once it is even: no change is in progress.
    private static long StableVersion(byte* node)
    {
        var spinner = default(SpinWait);
        var version = Volatile.Read(ref VersionOf(node));

        while ((version & 1) != 0)
        {
            spinner.SpinOnce();
            version = Volatile.Read(ref VersionOf(node));
        }

        return version;
    }

    // Whether the node has not changed since its version was read: the loads of the node are done
    // before the version is loaded again (see the class).
    private static bool IsValid(byte* node, long version)
    {
        Volatile.ReadBarrier();

        return Volatile.Read(ref VersionOf(node)) == version;
    }

    private void BeginChange(ulong address)
    {
        Interlocked.Increment(ref VersionOf(_arena.Payload(address)));
        _changing[_changingCount++] = address;
    }

    // The versions made odd become even, and what left the tree is retired: no reader that enters
    // from now on can reach it, and one that read it fails its validation.
    private void EndPublication()
    {
        for (var index = _changingCount - 1; index >= 0; index--)
        {
            ref var version = ref VersionOf(_arena.Payload(_changing[index]));

            Volatile.Write(ref version, version + 1);
        }

        for (var index = 0; index < _removedCount; index++)
        {
            _retired.Retire(_removed[index]);
            _nodes--;
        }

        _changingCount = 0;
        _removedCount = 0;
    }

    private void Removed(ulong address) => _removed[_removedCount++] = address;

    private void SplitLeaf(ulong leafAddress, byte* leaf, int count, int position, in TEntry entry)
    {
        var level = _height - 1;
        var right = TakeNode(leaf: true);
        var rightNode = _arena.Payload(right);

        if (position == count && IsRightmost(level))
        {
            At(rightNode, 0) = entry;
            CountOf(rightNode) = 1;
        }
        else
        {
            var total = count + 1;
            var leftCount = total / 2;

            for (var index = leftCount; index < total; index++)
            {
                At(rightNode, index - leftCount) = index < position ? At(leaf, index) : index == position ? entry : At(leaf, index - 1);
            }

            CountOf(rightNode) = total - leftCount;

            if (position < leftCount)
            {
                ShiftEntries(leaf, position, leftCount - 1, 1);
                At(leaf, position) = entry;
            }

            CountOf(leaf) = leftCount;
        }

        InsertIntoParent(level - 1, leafAddress, right, At(rightNode, 0));
    }

    // The child at the path's slot of this level was split, in place, and right goes beside it; the
    // inner node of this level takes the separator and right in place, or is split in place itself.
    private void InsertIntoParent(int level, ulong left, ulong right, TEntry separator)
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
        var parentChildren = Children(parent);

        if (count < _shape.InnerCapacity)
        {
            ShiftEntries(parent, slot, count, 1);
            At(parent, slot) = separator;

            // A word at a time, from the end, so that every load of a child reads a whole address.
            for (var index = count + 1; index > slot + 1; index--)
            {
                parentChildren[index] = parentChildren[index - 1];
            }

            parentChildren[slot + 1] = right;
            CountOf(parent) = count + 1;

            return;
        }

        if (slot == count && IsRightmost(level))
        {
            var alone = TakeNode(leaf: false);

            CountOf(_arena.Payload(alone)) = 0;
            Children(_arena.Payload(alone))[0] = right;
            InsertIntoParent(level - 1, parentAddress, alone, separator);

            return;
        }

        var separators = _separators.AsSpan(0, count + 1);
        var children = _children.AsSpan(0, count + 2);

        for (var index = 0; index <= count; index++)
        {
            separators[index] = index < slot ? At(parent, index) : index == slot ? separator : At(parent, index - 1);
        }

        for (var index = 0; index <= count + 1; index++)
        {
            children[index] = index <= slot ? parentChildren[index] : index == slot + 1 ? right : parentChildren[index - 1];
        }

        var middle = (count + 1) / 2;
        var newRight = TakeNode(leaf: false);

        WriteInner(_arena.Payload(newRight), separators[(middle + 1)..], children[(middle + 1)..]);
        WriteInner(parent, separators[..middle], children[..(middle + 1)]);
        InsertIntoParent(level - 1, parentAddress, newRight, separators[middle]);
    }

    // A leaf with too few entries after the delete takes in a neighbour under the same parent, or is
    // taken in by it, when both fit three quarters of a leaf. The left one of the pair stays.
    private bool TryMergeLeaf(byte* leaf, int count, int position)
    {
        var level = _height - 1;
        var parentAddress = _path[level - 1];
        var parent = _arena.Payload(parentAddress);
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

        var leafAddress = _path[level];

        for (var changing = 0; changing < level; changing++)
        {
            BeginChange(_path[changing]);
        }

        BeginChange(leafAddress);
        BeginChange(neighbourAddress);

        var first = Math.Min(slot, neighbourSlot);
        ulong kept;

        if (neighbourSlot < slot)
        {
            // The neighbour on the left takes the leaf's entries but the deleted one.
            CopyEntries(leaf, 0, neighbour, neighbourCount, position);
            CopyEntries(leaf, position + 1, neighbour, neighbourCount + position, count - position - 1);
            CountOf(neighbour) = neighbourCount + count - 1;
            kept = neighbourAddress;
            Removed(leafAddress);
        }
        else
        {
            ShiftEntries(leaf, position + 1, count, -1);
            CopyEntries(neighbour, 0, leaf, count - 1, neighbourCount);
            CountOf(leaf) = count - 1 + neighbourCount;
            kept = leafAddress;
            Removed(neighbourAddress);
        }

        RemoveRightOfPair(level - 1, first, kept);

        return true;
    }

    // Of children first and first + 1 of the inner node of this level, the right one went away into
    // the left one: the separator between them and the right child leave the node, in place; a root
    // left with one child gives way to it.
    private void RemoveRightOfPair(int level, int first, ulong kept)
    {
        var parentAddress = _path[level];
        var parent = _arena.Payload(parentAddress);
        var count = CountOf(parent);

        if (count == 1 && level == 0)
        {
            Volatile.Write(ref _root, kept);
            _height--;
            Removed(parentAddress);

            return;
        }

        RemoveFromInner(parent, first, first + 1);
    }

    // The child at the path's slot of this level goes away (its versions are odd already).
    private void RemoveChild(int level)
    {
        var parentAddress = _path[level];
        var parent = _arena.Payload(parentAddress);
        var count = CountOf(parent);
        var slot = _slots[level];

        if (count == 0)
        {
            Removed(parentAddress);

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

        if (count == 1 && level == 0)
        {
            Volatile.Write(ref _root, Children(parent)[1 - slot]);
            _height--;
            Removed(parentAddress);

            return;
        }

        RemoveFromInner(parent, slot > 0 ? slot - 1 : 0, slot);
    }

    // Takes one separator and one child out of an inner node, in place, the children a word at a time.
    private void RemoveFromInner(byte* node, int separator, int child)
    {
        var count = CountOf(node);
        var children = Children(node);

        ShiftEntries(node, separator + 1, count, -1);

        for (var index = child; index < count; index++)
        {
            children[index] = children[index + 1];
        }

        CountOf(node) = count - 1;
    }

    private void WriteInner(byte* node, ReadOnlySpan<TEntry> separators, ReadOnlySpan<ulong> children)
    {
        for (var index = 0; index < separators.Length; index++)
        {
            At(node, index) = separators[index];
        }

        var target = Children(node);

        for (var index = 0; index < children.Length; index++)
        {
            target[index] = children[index];
        }

        CountOf(node) = separators.Length;
    }

    private int FirstAbove(byte* node, int count, in TEntry probe)
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

    private int FirstNotBelow(byte* node, int count, in TEntry probe)
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

    private int LowerBound(byte* node, int count, in TEntry entry) => FirstNotBelow(node, count, entry);

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

            var slot = FirstAbove(node, CountOf(node), entry);

            _slots[level] = slot;
            address = Children(node)[slot];
        }
    }

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
        VersionOf(node) = 0;

        return address;
    }

    private void ValidateNode(ulong address, int level, bool hasLower, TEntry lower, bool hasUpper, TEntry upper, List<TEntry> entries, ref long nodes)
    {
        nodes++;

        var node = _arena.Payload(address);
        var count = CountOf(node);

        if ((VersionOf(node) & 1) != 0)
        {
            throw new InvalidOperationException("A node is left in the middle of a change.");
        }

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

    // Moves entries [from, to) by delta places within the node; torn states are hidden by the version.
    private static void ShiftEntries(byte* node, int from, int to, int delta)
    {
        if (to <= from)
        {
            return;
        }

        var entries = (TEntry*)(node + NodeShape.HeaderBytes);
        var bytes = (to - from) * sizeof(TEntry);

        Buffer.MemoryCopy(&entries[from], &entries[from + delta], bytes, bytes);
    }

    private static void CopyEntries(byte* from, int fromIndex, byte* to, int toIndex, int count) =>
        Buffer.MemoryCopy(&((TEntry*)(from + NodeShape.HeaderBytes))[fromIndex], &((TEntry*)(to + NodeShape.HeaderBytes))[toIndex], count * sizeof(TEntry), count * sizeof(TEntry));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref int CountOf(byte* node) => ref *(int*)node;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsLeaf(byte* node) => *(int*)(node + 4) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref long VersionOf(byte* node) => ref *(long*)(node + 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref TEntry At(byte* node, int index) => ref ((TEntry*)(node + NodeShape.HeaderBytes))[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong* Children(byte* node) => (ulong*)(node + _shape.ChildrenOffset);
}
