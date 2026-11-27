using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Storage.Hashing;

// The one hash set the engine owns: an open-addressing set on its own array, charged by the bytes
// of that array when it grows (docs/design/storage-and-maintenance.md, the memory budget). The key
// set of a child's publication keys, which it keeps between its transactions, and the index of the
// collection tails by root handle use it. Only the thread that holds the lock of the writes uses
// it; it takes no lock.
internal sealed class ChargedSet<T>
{
    public ChargedSet(IEqualityComparer<T> comparer, MemoryBudget budget) =>
        throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    public bool Contains(T value) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // The element equal to the probe, as the set holds it.
    public bool TryGet(T probe, out T value) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // False when an equal element is already held.
    public bool Add(T value) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    public bool Remove(T value) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Grows the array, charged before it is allocated, so that count elements fit.
    public void EnsureCapacity(int count) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Removes every element and keeps the array.
    public void Clear() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    public void ReportHeld(List<HeldArray> held) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");
}
