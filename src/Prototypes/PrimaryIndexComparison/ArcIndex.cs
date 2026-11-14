using Arc.Collections;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal readonly struct ArcIndex<TKey> : IPrimaryIndex<ArcIndex<TKey>, TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly UnorderedMap<TKey, ulong> _map;

    private ArcIndex(int capacity) => _map = new(capacity);

    public static ArcIndex<TKey> Create(int capacity) => new(capacity);
    public int Count => _map.Count;
    public int Capacity => _map.Capacity;
    public object Storage => _map;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Insert(TKey key, ulong value) => _map.Add(key, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(TKey key, ulong value) => _map[key] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out ulong value) => _map.TryGetValue(key, out value);

    public bool Remove(TKey key) => _map.Remove(key);
}
