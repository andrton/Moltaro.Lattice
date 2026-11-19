namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

// The order of fixed entries: by key, then by identity. A probe is an entry; a search for a key starts
// at the key with identity 0, below every identity (identities start at 1).
internal readonly struct FixedOrder<TEntry, TKey> : IEntryOrder<TEntry, TEntry>
    where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
    where TKey : IComparable<TKey>
{
    public int Compare(in TEntry left, in TEntry right)
    {
        var key = left.Key.CompareTo(right.Key);

        return key != 0 ? key : left.Id.CompareTo(right.Id);
    }

    public int CompareProbe(in TEntry probe, in TEntry entry) => Compare(probe, entry);

    public bool SameKey(in TEntry probe, in TEntry entry) => probe.Key.CompareTo(entry.Key) == 0;

    public TEntry ProbeOf(in TEntry entry) => entry;

    public ulong IdOf(in TEntry entry) => entry.Id;
}
