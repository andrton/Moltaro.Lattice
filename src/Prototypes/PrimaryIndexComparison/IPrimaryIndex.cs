namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;


// Struct adapters keep generic hot loops free of interface boxing. This is a
// benchmark contract, not an engine API or a custom hash implementation.
internal interface IPrimaryIndex<TSelf, TKey>
    where TSelf : struct, IPrimaryIndex<TSelf, TKey>
    where TKey : notnull, IEquatable<TKey>
{
    static abstract TSelf Create(int capacity);

    int Count { get; }
    int Capacity { get; }
    object Storage { get; }

    void Insert(TKey key, ulong value);
    void Set(TKey key, ulong value);
    bool TryGet(TKey key, out ulong value);
    bool Remove(TKey key);
}
