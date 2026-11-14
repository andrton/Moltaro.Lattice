using Arc.Collections;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

// Experimental capacity owner around an unchanged package map. It is not a
// database write API: inputs are already validated and new keys are unique.
internal sealed class ReservedArcIndex : IDisposable
{
    // Lock order: mutations -> gate. Readers take only gate in read mode.
    // Mutations serializes index maintenance, not application write preparation.
    // Building an equivalent replacement holds mutations but not the reader gate.
    // No lock spans the simulated commit wait or any I/O.
    private readonly object _mutations = new();
    private readonly ReaderWriterLockSlim _gate = new();
    private UnorderedMapSlim<long, ulong> _map = new(16);
    private int _reservedSlots;
    private const int MaximumSlots = 4096;

    public void Reserve(int slots)
    {
        lock (_mutations)
        {
            var required = checked(_map.Count + _reservedSlots + slots);
            if (slots < 0 || required > MaximumSlots)
            {
                throw new InvalidOperationException("Prototype capacity budget exceeded before commit.");
            }

            UnorderedMapSlim<long, ulong>? replacement = null;
            if (required > _map.Capacity)
            {
                replacement = new UnorderedMapSlim<long, ulong>((uint)required);
                foreach (var entry in _map)
                {
                    replacement.TryAdd(entry.Key, entry.Value);
                }
            }

            // Readers can use the old index throughout the allocation/copy.
            // Briefly exclude them only to install equivalent physical metadata.
            _gate.EnterWriteLock();
            try
            {
                if (replacement is not null)
                {
                    _map = replacement;
                }

                _reservedSlots += slots;
            }
            finally
            {
                _gate.ExitWriteLock();
            }
        }
    }

    public void Release(int slots)
    {
        lock (_mutations)
        {
            _gate.EnterWriteLock();
            try
            {
                IntegrationChecks.Require(slots >= 0 && slots <= _reservedSlots, "Invalid reservation release.");
                _reservedSlots -= slots;
            }
            finally
            {
                _gate.ExitWriteLock();
            }
        }
    }

    public void AddReserved(long key, ulong locator)
    {
        lock (_mutations)
        {
            _gate.EnterWriteLock();
            try
            {
                IntegrationChecks.Require(_reservedSlots > 0 && _map.Count < _map.Capacity,
                    "No prepared slot for publication.");
                IntegrationChecks.Require(_map.TryAdd(key, locator), "Duplicate key in prepared input.");
                _reservedSlots--;
            }
            finally
            {
                _gate.ExitWriteLock();
            }
        }
    }

    public bool TryGet(long key, out ulong locator)
    {
        _gate.EnterReadLock();
        try
        {
            return _map.TryGetValue(key, out locator);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public bool Remove(long key)
    {
        lock (_mutations)
        {
            _gate.EnterWriteLock();
            try
            {
                return _map.Remove(key);
            }
            finally
            {
                _gate.ExitWriteLock();
            }
        }
    }

    public (int Count, int Capacity, int Reserved) Inspect()
    {
        _gate.EnterReadLock();
        try
        {
            return (_map.Count, _map.Capacity, _reservedSlots);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void Dispose() => _gate.Dispose();
}
