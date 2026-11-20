namespace Moltaro.Lattice.Engine.Indexes;

// What a search of the tree of an index looks for, as the order of the index compares it with an entry
// (OrderedTree): the key of a search (SearchKey), or an entry, for the writer and for a reader that
// descends again at a fence. Only the constraint of the one binary search and the one descent of the
// tree, which the JIT compiles for each kind of probe, so that no call goes through the interface.
internal interface ITreeProbe<TProbe>
    where TProbe : ITreeProbe<TProbe>, allows ref struct
{
    // The order of the probe against the entry, by the order of the index given: below zero when the
    // probe comes first. Static, so that the probe is passed by reference at every comparison and never
    // copied, as an instance member called on a parameter passed by reference would be.
    static abstract int Compare(IndexOrder order, scoped in TProbe probe, ReadOnlySpan<byte> entry);
}
