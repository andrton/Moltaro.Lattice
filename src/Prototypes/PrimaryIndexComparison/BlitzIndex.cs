using Faster.Map.Core;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal readonly struct BlitzIndex<TKey> : IPrimaryIndex<BlitzIndex<TKey>, TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly BlitzMap<TKey, ulong> _map;

    private BlitzIndex(int capacity) => _map = new(capacity);

    public static BlitzIndex<TKey> Create(int capacity) => new(capacity);
    public int Count => _map.Count;
    public int Capacity => _map.Size;
    public object Storage => _map;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Insert(TKey key, ulong value) => _map.Insert(key, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(TKey key, ulong value) => _map[key] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out ulong value) => _map.Get(key, out value);

    public bool Remove(TKey key) => _map.Remove(key);
}
