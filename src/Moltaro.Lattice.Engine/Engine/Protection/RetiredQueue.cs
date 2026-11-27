using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine.Protection;

// The retirement queue of RetiredMemory on a ring of its own: retired items in the order they were
// retired, taken from the head once the reads that could reach them have left. The ring is charged
// by its bytes when it grows (docs/design/storage-and-maintenance.md, the memory budget); room
// ensured before a publication lets the publication enqueue without allocating. Only the thread
// that holds the lock of the writes uses it; it takes no lock.
//
// The items are _ring[_head] up to Count places on, wrapping at the end of the ring. A growth copies
// them in their order to the start of the new ring. A place an item leaves is cleared, so that the
// ring keeps no owner of a freed thing alive.
internal sealed class RetiredQueue
{
    private RetiredItem[] _ring = [];

    private int _head;

    public int Count { get; private set; }

    // The length of the ring: the items it holds without growing.
    public int Capacity => _ring.Length;

    // The item that many places after the head.
    public ref readonly RetiredItem this[int index] => ref _ring[(_head + index) % _ring.Length];

    // Under the room ensured: adds the item at the tail without allocating.
    public void Enqueue(RetiredItem item)
    {
        System.Diagnostics.Debug.Assert(Count < _ring.Length, "A retirement is enqueued only into the room ensured for it.");
        _ring[(_head + Count) % _ring.Length] = item;
        Count++;
    }

    // The item at the head, which the queue keeps.
    public RetiredItem Peek() => _ring[_head];

    // Removes the item at the head and returns it.
    public RetiredItem Dequeue()
    {
        var item = _ring[_head];
        _ring[_head] = default;
        _head = (_head + 1) % _ring.Length;
        Count--;
        return item;
    }

    // Grows the ring, charged before it is allocated, so that it holds count items in all; the items
    // keep their order. A refusal leaves the ring and its items as they were.
    public void EnsureRoom(int count, MemoryBudget budget)
    {
        if (count <= _ring.Length)
        {
            return;
        }

        var grown = budget.Allocate<RetiredItem>(Math.Max(count, _ring.Length == 0 ? 4 : 2 * _ring.Length));
        for (var index = 0; index < Count; index++)
        {
            grown[index] = this[index];
        }

        var previous = _ring;
        _ring = grown;
        _head = 0;
        budget.Release(previous);
    }

    // When the queue holds nothing: releases its ring, as RetiredMemory trims a queue that held
    // published retirements.
    public void TrimWhenEmpty(MemoryBudget budget)
    {
        if (Count != 0)
        {
            return;
        }

        budget.Release(_ring);
        _ring = [];
        _head = 0;
    }

    // Forgets every item and keeps the ring, at the close.
    public void Clear()
    {
        Array.Clear(_ring);
        _head = 0;
        Count = 0;
    }

    public void ReportHeld(List<HeldArray> held)
    {
        if (_ring.Length != 0)
        {
            held.Add(new HeldArray(nameof(RetiredQueue), typeof(RetiredItem), _ring.Length));
        }
    }
}
