using System.Runtime.CompilerServices;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

// Candidate C: a skip list with one writer and readers without a lock, as the memtables of LevelDB and
// RocksDB keep it.
//
// Modelled on, not adapted from (no code is ported):
// - LevelDB (https://github.com/google/leveldb, BSD-3-Clause, commit
//   29465588d3dfdc80fba5e198037582b8fdb5dd60, db/skiplist.h): a node is the entry and an array of next
//   links, one per level of its random height (one in four rises a level, at most 12 levels); a
//   reader loads a link with acquire semantics; the writer fills the links of a new node first and
//   publishes it by a store with release semantics into each predecessor, bottom up; the height of
//   the list is read relaxed, since a reader that sees a stale height only starts lower. Prev is a
//   new search from the head for the last entry below.
// - RocksDB (https://github.com/facebook/rocksdb, read under Apache-2.0, commit
//   09fe540613c0777e666411e7dd54aa84a535be6b, memtable/inlineskiplist.h): the entry inline in the node
//   (one hop less than LevelDB's pointer to an encoded entry), and the node allocated with its height
//   before the insert that links it (AllocateKey).
//
// What neither has, because a memtable never deletes a node: Remove. With one writer it unlinks the
// node from the top level down, each by one store with release semantics into the predecessor, and
// never changes the removed node's own links, so a reader standing on it goes on to entries that were
// after it; the node is retired to the epoch. The height is kept in the node (RocksDB keeps none) so
// that the writer knows how far up it is linked.
//
// A node is one block of the arena: its header of 32 bytes, the entry, the height (8 bytes, for the
// alignment of the links) and the links of eight bytes each.
internal sealed unsafe class SkipList<TEntry, TProbe, TOrder> : IOrderedIndex<TEntry, TProbe>
    where TEntry : unmanaged
    where TProbe : unmanaged
    where TOrder : struct, IEntryOrder<TEntry, TProbe>
{
    private const int MostLevels = 12;

    private readonly NodeArena _arena;

    private readonly ReadEpochs _epochs;

    private readonly RetiredBlocks _retired;

    private readonly TOrder _order;

    private readonly ulong _head;

    // Prepared nodes of a drawn height, one for each prepared insert.
    private readonly Stack<ulong> _pool = new();

    private readonly ulong[] _before = new ulong[MostLevels];

    // The writer's generator of heights; seeded, so a run is repeatable.
    private readonly Random _heights = new(20261118);

    private readonly int _linksOffset;

    private int _height = 1;

    private long _count;

    private long _nodeBytes;

    private int _preparedWrites;

    public SkipList(NodeArena arena, ReadEpochs epochs, TOrder order)
    {
        _arena = arena;
        _epochs = epochs;
        _retired = new RetiredBlocks(epochs, arena);
        _order = order;
        _linksOffset = ((sizeof(TEntry) + 7) & ~7) + 8;
        _head = _arena.Allocate(PayloadFor(MostLevels), prepared: false);

        var head = _arena.Payload(_head);

        HeightOf(head) = MostLevels;

        for (var level = 0; level < MostLevels; level++)
        {
            Links(head)[level] = 0;
        }

        _nodeBytes = _arena.SlotOf(_head);
    }

    public string Name => "SkipList";

    public long Count => _count;

    public StructureBytes Bytes => new(_nodeBytes, 0, _count + 1, _height);

    public void Prepare(int writes)
    {
        _preparedWrites += writes;

        while (_pool.Count < _preparedWrites)
        {
            var height = RandomHeight();
            var node = _arena.Allocate(PayloadFor(height), prepared: true);

            HeightOf(_arena.Payload(node)) = height;
            _pool.Push(node);
        }

        _retired.Reserve(_preparedWrites);
    }

    public void Withdraw(int writes)
    {
        _preparedWrites -= writes;

        while (_pool.Count > _preparedWrites)
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

    public int Read(in TProbe from, bool inclusive, bool forward, bool keyOnly, Span<TEntry> into)
    {
        var slot = _epochs.Enter();

        try
        {
            var copied = 0;

            if (forward)
            {
                for (var node = FirstFrom(from, inclusive); node != 0 && copied < into.Length; node = Volatile.Read(ref Links(_arena.Payload(node))[0]))
                {
                    ref var entry = ref EntryOf(_arena.Payload(node));

                    if (keyOnly && !_order.SameKey(from, entry))
                    {
                        break;
                    }

                    into[copied++] = entry;
                }

                return copied;
            }

            // Backwards each next entry is a new search from the head, as LevelDB's Prev.
            var target = from;
            var targetInclusive = inclusive;

            while (copied < into.Length)
            {
                var node = LastBefore(target, targetInclusive);

                if (node == _head)
                {
                    break;
                }

                ref var entry = ref EntryOf(_arena.Payload(node));

                if (keyOnly && !_order.SameKey(from, entry))
                {
                    break;
                }

                into[copied++] = entry;
                target = _order.ProbeOf(entry);
                targetInclusive = false;
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

        var existing = FindForWriter(entry);

        if (existing != 0 && _order.Compare(entry, EntryOf(_arena.Payload(existing))) == 0)
        {
            throw new InvalidOperationException("The index holds this key with this identity already.");
        }

        if (!_pool.TryPop(out var address))
        {
            throw new InvalidOperationException("A publication took more nodes than its write prepared.");
        }

        _arena.Publish(address);

        var node = _arena.Payload(address);
        var height = HeightOf(node);

        EntryOf(node) = entry;

        if (height > _height)
        {
            for (var level = _height; level < height; level++)
            {
                _before[level] = _head;
            }

            // Relaxed, as LevelDB's max_height_: a reader that sees the new height before the links finds
            // null links from the head and goes down.
            Volatile.Write(ref _height, height);
        }

        for (var level = 0; level < height; level++)
        {
            Links(node)[level] = Links(_arena.Payload(_before[level]))[level];
        }

        // Bottom up, each a store with release semantics: a reader that sees the node at a level sees
        // its entry and its links.
        for (var level = 0; level < height; level++)
        {
            Volatile.Write(ref Links(_arena.Payload(_before[level]))[level], address);
        }

        _count++;
        _nodeBytes += _arena.SlotOf(address);
    }

    public bool Remove(in TEntry entry)
    {
        TakeWrite();

        var address = FindForWriter(entry);

        if (address == 0 || _order.Compare(entry, EntryOf(_arena.Payload(address))) != 0)
        {
            return false;
        }

        var node = _arena.Payload(address);

        // Top down: once the store at level 0 is done no reader that enters can reach the node; one
        // that stands on it goes on through its links, which are not changed.
        for (var level = HeightOf(node) - 1; level >= 0; level--)
        {
            ref var link = ref Links(_arena.Payload(_before[level]))[level];

            if (link == address)
            {
                Volatile.Write(ref link, Links(node)[level]);
            }
        }

        _nodeBytes -= _arena.SlotOf(address);
        _retired.Retire(address);
        _count--;

        return true;
    }

    public List<TEntry> Validate()
    {
        var entries = new List<TEntry>((int)_count);

        for (var node = Links(_arena.Payload(_head))[0]; node != 0; node = Links(_arena.Payload(node))[0])
        {
            var entry = EntryOf(_arena.Payload(node));

            if (entries.Count > 0 && _order.Compare(entries[^1], entry) >= 0)
            {
                throw new InvalidOperationException("The skip list is out of order.");
            }

            entries.Add(entry);
        }

        for (var level = 1; level < MostLevels; level++)
        {
            var previous = _head;

            for (var node = Links(_arena.Payload(_head))[level]; node != 0; node = Links(_arena.Payload(node))[level])
            {
                if (HeightOf(_arena.Payload(node)) <= level
                    || (previous != _head && _order.Compare(EntryOf(_arena.Payload(previous)), EntryOf(_arena.Payload(node))) >= 0))
                {
                    throw new InvalidOperationException($"Level {level} of the skip list is broken.");
                }

                previous = node;
            }
        }

        if (entries.Count != _count)
        {
            throw new InvalidOperationException($"The skip list counts {_count} entries and holds {entries.Count}.");
        }

        return entries;
    }

    // For a reader: the first node at or above the probe (inclusive) or above it: the link of level 0
    // that the last comparison read. Not the link loaded again after the loop: the writer may have put a
    // node below the probe behind the same predecessor meanwhile, and a walk from it would go back.
    private ulong FirstFrom(in TProbe probe, bool inclusive)
    {
        var node = _head;
        var next = 0UL;

        for (var level = Volatile.Read(ref _height) - 1; level >= 0; level--)
        {
            while (true)
            {
                next = Volatile.Read(ref Links(_arena.Payload(node))[level]);

                if (next == 0)
                {
                    break;
                }

                var order = _order.CompareProbe(probe, EntryOf(_arena.Payload(next)));

                if (order < 0 || (order == 0 && inclusive))
                {
                    break;
                }

                node = next;
            }
        }

        return next;
    }

    // For a reader: the last node at or below the probe (inclusive) or below it; the head when none.
    private ulong LastBefore(in TProbe probe, bool inclusive)
    {
        var node = _head;

        for (var level = Volatile.Read(ref _height) - 1; level >= 0; level--)
        {
            while (true)
            {
                var next = Volatile.Read(ref Links(_arena.Payload(node))[level]);

                if (next == 0)
                {
                    break;
                }

                var order = _order.CompareProbe(probe, EntryOf(_arena.Payload(next)));

                if (order < 0 || (order == 0 && !inclusive))
                {
                    break;
                }

                node = next;
            }
        }

        return node;
    }

    // For the writer: the predecessors of the entry on every level in _before, and the first node at
    // or above it.
    private ulong FindForWriter(in TEntry entry)
    {
        var node = _head;

        for (var level = MostLevels - 1; level >= 0; level--)
        {
            while (true)
            {
                var next = Links(_arena.Payload(node))[level];

                if (next == 0 || _order.Compare(entry, EntryOf(_arena.Payload(next))) <= 0)
                {
                    break;
                }

                node = next;
            }

            _before[level] = node;
        }

        return Links(_arena.Payload(node))[0];
    }

    private int RandomHeight()
    {
        var height = 1;

        while (height < MostLevels && _heights.Next(4) == 0)
        {
            height++;
        }

        return height;
    }

    private void TakeWrite()
    {
        if (_preparedWrites == 0)
        {
            throw new InvalidOperationException("A publication needs a prepared write.");
        }

        _preparedWrites--;
    }

    private int PayloadFor(int height) => _linksOffset + (height * 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref TEntry EntryOf(byte* node) => ref *(TEntry*)node;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref int HeightOf(byte* node) => ref *(int*)(node + _linksOffset - 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ulong* Links(byte* node) => (ulong*)(node + _linksOffset);
}
