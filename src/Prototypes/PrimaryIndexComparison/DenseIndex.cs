using Faster.Map.Core;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal readonly struct DenseIndex<TKey> : IPrimaryIndex<DenseIndex<TKey>, TKey>
    where TKey : notnull, IEquatable<TKey>
{
    private readonly DenseMap<TKey, ulong> _map;

    private DenseIndex(int capacity) => _map = new((uint)capacity);

    public static DenseIndex<TKey> Create(int capacity) => new(capacity);
    public int Count => _map.Count;
    public int Capacity => checked((int)_map.Size);
    public object Storage => _map;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Insert(TKey key, ulong value) => _map.Insert(key, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(TKey key, ulong value) => _map[key] = value;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGet(TKey key, out ulong value) => _map.Get(key, out value);

    public bool Remove(TKey key) => _map.Remove(key);
}
