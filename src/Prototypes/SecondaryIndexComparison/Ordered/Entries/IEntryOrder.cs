namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

// The order of the entries of one index, and of a search against them. An entry is unique by (key,
// identity): Compare orders by the key and then by the identity, so a group of equal keys is a run
// ordered by identity. A probe is what a reader searches with: the entry itself for a fixed key, the
// text and an identity for a text key whose entry does not hold the text. The order is a struct, so
// that a structure generic over it gets its comparisons inlined, as the engine's read path needs.
//
// CompareProbe and SameKey may read memory of the arena (the text of an entry or of its root), so
// they are called inside the protection of reads, and so is ProbeOf, whose probe may point there.
internal interface IEntryOrder<TEntry, TProbe>
    where TEntry : unmanaged
    where TProbe : unmanaged
{
    // For the writer: the order of two entries.
    int Compare(in TEntry left, in TEntry right);

    // The order of a probe against an entry: below zero when the probe comes first.
    int CompareProbe(in TProbe probe, in TEntry entry);

    // Whether the entry has the key of the probe, whatever its identity.
    bool SameKey(in TProbe probe, in TEntry entry);

    // A probe at the place of the entry: for a walk that goes on from a separator.
    TProbe ProbeOf(in TEntry entry);

    // The identity of an entry.
    ulong IdOf(in TEntry entry);
}
