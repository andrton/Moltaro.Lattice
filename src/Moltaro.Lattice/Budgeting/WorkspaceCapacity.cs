namespace Moltaro.Lattice.Budgeting;

// Scratch used repeatedly under one writes lock. Remember prior arrays while a queued use
// may still be withdrawn, and retain the smallest backing that serves the remaining uses.
internal struct WorkspaceCapacity<T>
{
    private BackingHistory<T[]>? _earlier;
    private int _installed;

    public T[] Grow(T[] previous, int length, MemoryBudget budget, string owner)
    {
        var replacement = budget.Allocate<T>(length, owner);
        try
        {
            previous.CopyTo(replacement, 0);
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

    public void Drop(ref T[] backing, MemoryBudget budget)
    {
        _installed = 0;
        ReleaseUnused(ref backing, 0, budget);
    }

    public void ReleaseUnused(ref T[] backing, int inUse, MemoryBudget budget)
    {
        var needed = Math.Max(inUse, _installed);
        while (_earlier is { } previous && previous.Backing.Length >= needed)
        {
            var released = backing;
            backing = previous.Backing;
            _earlier = previous.Earlier;
            previous.Drop(budget);
            budget.Release(released);
        }

        if (needed == 0)
        {
            var released = backing;
            backing = [];
            budget.Release(released);
            return;
        }

        var earlier = _earlier;
        if (earlier is null)
        {
            return;
        }

        if (earlier.Backing.Length < _installed)
        {
            _earlier = null;
        }
        else
        {
            while (earlier.Earlier is { } smaller && smaller.Backing.Length >= _installed)
            {
                earlier = smaller;
            }

            var retained = earlier;
            earlier = retained.Earlier;
            retained.Earlier = null;
        }

        while (earlier is not null)
        {
            var abandoned = earlier;
            earlier = abandoned.Drop(budget);
            budget.Release(abandoned.Backing);
        }
    }
}
