using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Budgeting;

// Optional backing inventory, used only by tests and measurements. A retained write batch
// can give up its capacity from its lease's finalizer, so this table must itself be usable
// during finalization. Weak targets also allow an abandoned batch to be collected.
internal sealed class BudgetBackings
{
    private readonly Dictionary<int, Backing> _byHash = [];

    public void Add(object value, string owner)
    {
        var hash = RuntimeHelpers.GetHashCode(value);
        _byHash.TryGetValue(hash, out var previous);
        _byHash[hash] = new Backing(new WeakReference<object>(value), owner, previous);
    }

    public void Remove(object value)
    {
        var hash = RuntimeHelpers.GetHashCode(value);
        if (!_byHash.TryGetValue(hash, out var first))
        {
            return;
        }

        Backing? previous = null;
        for (var backing = first; backing is not null; backing = backing.Next)
        {
            if (!backing.Value.TryGetTarget(out var target) || ReferenceEquals(target, value))
            {
                if (previous is not null)
                {
                    previous.Next = backing.Next;
                }
                else if (backing.Next is { } next)
                {
                    _byHash[hash] = next;
                }
                else
                {
                    _byHash.Remove(hash);
                }
            }
            else
            {
                previous = backing;
            }
        }
    }

    public void Visit(Action<string, object> visit)
    {
        foreach (var first in _byHash.Values)
        {
            for (var backing = first; backing is not null; backing = backing.Next)
            {
                if (backing.Value.TryGetTarget(out var value))
                {
                    visit(backing.Owner, value);
                }
            }
        }
    }

    public void Clear() => _byHash.Clear();

    private sealed class Backing(WeakReference<object> value, string owner, Backing? next)
    {
        public WeakReference<object> Value { get; } = value;
        public string Owner { get; } = owner;
        public Backing? Next { get; set; } = next;
    }
}
