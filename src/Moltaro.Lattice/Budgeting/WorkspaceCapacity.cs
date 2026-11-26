namespace Moltaro.Lattice.Budgeting;

// Backing reused under one writes lock. Keep earlier arrays while queued use
// might be withdrawn, and leave the smallest paid capacity that serves the
// installed and remaining prepared work. Each caller owns its data copying.
internal struct WorkspaceCapacity<T>
{
    private BackingHistory<T[]>? _earlier;
    private int _installed;

    public bool HasEarlier => _earlier is not null;

    public bool CanRelease(int inUse) => HasEarlier || (_installed == 0 && inUse == 0);

    public T[] Grow(T[] previous, int length, MemoryBudget budget, string owner)
    {
        var replacement = AllocateGrowth(previous, length, budget, owner);
        previous.CopyTo(replacement, 0);
        return replacement;
    }

    public T[] AllocateGrowth(T[] previous, int length, MemoryBudget budget, string owner)
    {
        var replacement = budget.Allocate<T>(length, owner);
        try
        {
            if (previous.Length != 0)
            {
                _earlier = BackingHistory<T[]>.Keep(previous, _earlier, budget, owner);
            }

            return replacement;
        }
        catch
        {
            budget.Release(replacement);
            throw;
        }
    }

    public void Keep(int used) => _installed = Math.Max(_installed, used);

    public void Keep(int used, MemoryBudget budget)
    {
        if (used > _installed)
        {
            _installed = used;
            ReleaseSuperseded(budget);
        }
    }

    public void Reset() => _installed = 0;

    public void Drop(ref T[] backing, MemoryBudget budget)
    {
        Reset();
        ReleaseUnused(ref backing, 0, budget);
    }

    public void ReleaseUnused(ref T[] backing, int inUse, MemoryBudget budget)
    {
        var previous = backing;
        backing = SelectRetained(previous, inUse, budget);
        if (!ReferenceEquals(backing, previous))
        {
            budget.Release(previous);
        }
    }

    // The caller's current array stays paid until it installs the returned one.
    public T[] SelectRetained(T[] current, int inUse, MemoryBudget budget)
    {
        var needed = Math.Max(inUse, _installed);
        if (needed == 0)
        {
            ReleaseEarlier(ref _earlier, budget);
            return [];
        }

        var retained = current;
        while (_earlier is { } previous && previous.Backing.Length >= needed)
        {
            if (!ReferenceEquals(retained, current))
            {
                budget.Release(retained);
            }

            retained = previous.Backing;
            _earlier = previous.Drop(budget);
        }

        ReleaseSuperseded(budget);
        return retained;
    }

    private void ReleaseSuperseded(MemoryBudget budget)
    {
        ref var obsolete = ref _earlier;
        while (obsolete is { } retained && retained.Backing.Length >= _installed)
        {
            obsolete = ref retained.Earlier;
        }

        ReleaseEarlier(ref obsolete, budget);
    }

    private static void ReleaseEarlier(ref BackingHistory<T[]>? earlier, MemoryBudget budget)
    {
        while (earlier is { } backing)
        {
            earlier = backing.Drop(budget);
            budget.Release(backing.Backing);
        }
    }
}
