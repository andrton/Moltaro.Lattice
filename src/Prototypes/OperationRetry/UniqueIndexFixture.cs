using Arc.Collections;

namespace Moltaro.Lattice.Prototypes.OperationRetry;

// Tiny final-state validator over the selected package. It models the ordered
// writer stage; it does not perform SQL, native preparation or concurrent reads.
internal sealed class UniqueIndexFixture
{
    private readonly object _writer = new();
    private readonly UniqueKey?[] _roots = new UniqueKey?[32];
    private readonly UniqueKeyComparer _comparer;
    private readonly OrderedMultiMap<UniqueKey, int> _index;

    public UniqueIndexFixture(StringComparer? textComparer = null)
    {
        _comparer = new UniqueKeyComparer(textComparer ?? StringComparer.Ordinal);
        _index = new OrderedMultiMap<UniqueKey, int>(_comparer);
    }

    public bool TryApply(params UniqueUpdate[] updates)
    {
        lock (_writer)
        {
            if (updates.Any(update => update.Root < 0 || update.Root >= _roots.Length)
                || updates.Select(update => update.Root).Distinct().Count() != updates.Length)
            {
                throw new ArgumentException("Invalid bounded fixture roots.");
            }

            // Validate the projected end state. Existing keys owned by another
            // changed root are released by this same atomic batch, enabling swaps.
            for (var i = 0; i < updates.Length; i++)
            {
                if (updates[i].Key is not { ContainsNull: false } candidate)
                {
                    continue;
                }

                for (var j = 0; j < i; j++)
                {
                    if (updates[j].Key is { ContainsNull: false } prior && _comparer.Compare(candidate, prior) == 0)
                    {
                        return false;
                    }
                }

                for (var node = _index.FindFirstNode(candidate);
                    node is not null && _comparer.Compare(node.Key, candidate) == 0;
                    node = node.Next)
                {
                    if (!updates.Any(update => update.Root == node.Value))
                    {
                        return false;
                    }
                }
            }

            // All validation completes before publication. Production additionally
            // prepares/reuses nodes before commit; this managed model tests semantics.
            foreach (var update in updates)
            {
                if (_roots[update.Root] is { } old)
                {
                    var node = _index.FindNode(old, update.Root)
                        ?? throw new InvalidOperationException("Missing fixture index entry.");
                    _index.RemoveNode(node);
                }
            }

            foreach (var update in updates)
            {
                _roots[update.Root] = update.Key;
                if (update.Key is { } key)
                {
                    _index.Add(key, update.Root);
                }
            }

            return true;
        }
    }

    public UniqueKey? Read(int root)
    {
        lock (_writer)
        {
            return _roots[root];
        }
    }
}
