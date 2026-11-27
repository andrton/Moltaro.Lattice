using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine.Protection;

// The retirement queue of RetiredMemory on a ring of its own: retired items in the order they were
// retired, taken from the head once the reads that could reach them have left. The ring is charged
// by its bytes when it grows (docs/design/storage-and-maintenance.md, the memory budget); room
// ensured before a publication lets the publication enqueue without allocating. Only the thread
// that holds the lock of the writes uses it; it takes no lock.
internal sealed class RetiredQueue
{
    public int Count => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Under the room ensured: adds the item at the tail without allocating.
    public void Enqueue(RetiredItem item) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // The item at the head, which the queue keeps.
    public RetiredItem Peek() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Removes the item at the head and returns it.
    public RetiredItem Dequeue() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Grows the ring, charged before it is allocated, so that it holds count items in all; the items
    // keep their order. A refusal leaves the ring and its items as they were.
    public void EnsureRoom(int count, MemoryBudget budget) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // When the queue holds nothing: releases its ring, as RetiredMemory trims a queue that held
    // published retirements.
    public void TrimWhenEmpty(MemoryBudget budget) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    public void ReportHeld(List<HeldArray> held) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");
}
