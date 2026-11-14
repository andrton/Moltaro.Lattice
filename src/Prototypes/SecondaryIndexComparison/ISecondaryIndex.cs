namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

// Benchmark adapter only. Neither package enumerators nor nodes escape to an SDK.
internal interface ISecondaryIndex<TSelf> where TSelf : struct, ISecondaryIndex<TSelf>
{
    static abstract TSelf Create();
    object Storage { get; }
    int Count { get; }
    void Add(SearchKey key, ulong locator);
    bool TryFirst(SearchKey key, out ulong locator);
    ulong RangeSum(SearchKey lower, SearchKey upper);
    void Move(SearchKey oldKey, SearchKey newKey, ulong locator);
    IEnumerable<KeyValuePair<SearchKey, ulong>> Entries();
}
