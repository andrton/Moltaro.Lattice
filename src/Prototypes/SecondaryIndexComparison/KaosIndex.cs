using Kaos.Collections;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal readonly struct KaosIndex : ISecondaryIndex<KaosIndex>
{
    private readonly RankedMap<SearchKey, ulong> _map;
    private KaosIndex(RankedMap<SearchKey, ulong> map) => _map = map;
    public static KaosIndex Create() => new(new());
    public object Storage => _map;
    public int Count => _map.Count;
    public void Add(SearchKey key, ulong locator) => _map.Add(key, locator);

    public bool TryFirst(SearchKey key, out ulong locator)
    {
        var found = _map.TryGetGreaterThanOrEqual(key, out var entry) && entry.Key == key;
        locator = found ? entry.Value : 0;
        return found;
    }

    public ulong RangeSum(SearchKey lower, SearchKey upper)
    {
        // Deliberately use the lower-bound iterator plus an upper check, matching
        // Arc's adapter. ElementsBetween is evaluated separately in ApiChecks.
        ulong sum = 0;
        foreach (var entry in _map.ElementsFrom(lower))
        {
            if (entry.Key.CompareTo(upper) > 0)
            {
                break;
            }

            sum += entry.Value;
        }

        return sum;
    }

    public void Move(SearchKey oldKey, SearchKey newKey, ulong locator)
    {
        Check.Require(((ICollection<KeyValuePair<SearchKey, ulong>>)_map)
            .Remove(new(oldKey, locator)), "Kaos update target missing.");
        _map.Add(newKey, locator);
    }

    public IEnumerable<KeyValuePair<SearchKey, ulong>> Entries() => _map;
}
