using Arc.Collections;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal readonly struct ArcIndex : ISecondaryIndex<ArcIndex>
{
    private readonly OrderedMultiMap<SearchKey, ulong> _map;
    private ArcIndex(OrderedMultiMap<SearchKey, ulong> map) => _map = map;
    public static ArcIndex Create() => new(new());
    public object Storage => _map;
    public int Count => _map.Count;
    public void Add(SearchKey key, ulong locator) => _map.Add(key, locator);

    public bool TryFirst(SearchKey key, out ulong locator)
    {
        var node = _map.FindFirstNode(key);
        locator = node?.Value ?? 0;
        return node is not null;
    }

    public ulong RangeSum(SearchKey lower, SearchKey upper)
    {
        ulong sum = 0;
        for (var node = _map.GetLowerBound(lower); node is not null && node.Key.CompareTo(upper) <= 0; node = node.Next)
        {
            sum += node.Value;
        }

        return sum;
    }

    public void Move(SearchKey oldKey, SearchKey newKey, ulong locator)
    {
        var node = _map.FindNode(oldKey, locator)
            ?? throw new InvalidOperationException("Arc update target missing.");
        Check.Require(_map.SetNodeKey(node, newKey), "Arc key did not change.");
    }

    public IEnumerable<KeyValuePair<SearchKey, ulong>> Entries() => _map;
}
