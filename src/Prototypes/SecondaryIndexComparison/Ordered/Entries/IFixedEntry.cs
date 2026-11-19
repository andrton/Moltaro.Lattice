namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

// An entry of a fixed key and an identity of eight bytes, for the scenarios that measure fixed keys.
// The key is made from a 64-bit number: drawn at random for a random distribution, counted for an
// ascending one, the same for a hot key.
internal interface IFixedEntry<TSelf, TKey>
    where TSelf : unmanaged, IFixedEntry<TSelf, TKey>
    where TKey : IComparable<TKey>
{
    static abstract string KeyName { get; }

    static abstract TSelf Of(ulong number, ulong id);

    static abstract TSelf With(TKey key, ulong id);

    TKey Key { get; }

    ulong Id { get; }
}
