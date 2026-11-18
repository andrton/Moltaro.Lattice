namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

// What the scenarios ask of a candidate. One thread is the writer: it prepares, publishes and looks
// at the retired blocks. Any number of readers call Read at the same time, each call one step of a
// read inside the protection of reads, holding nothing afterwards.
internal interface IOrderedIndex<TEntry, TProbe> : IDisposable
    where TEntry : unmanaged
    where TProbe : unmanaged
{
    string Name { get; }

    long Count { get; }

    StructureBytes Bytes { get; }

    // Writer, before the publication of this many writes of one entry each: takes the nodes and the
    // places for retired blocks they can need, so that a publication allocates nothing.
    void Prepare(int writes);

    // Writer: prepared writes that will not be published give back what Prepare took for them.
    void Withdraw(int writes);

    // Writer, the publication of one prepared write: the entry goes in. The entry is not there yet.
    void Insert(in TEntry entry);

    // Writer, the publication of one prepared write: the entry goes out; false when it is not there.
    bool Remove(in TEntry entry);

    // Writer, after a publication: the look at the retired blocks when enough were retired.
    void AfterPublication();

    // Reader, one step: copies into the span, in order, up to its length of entries from the probe on
    // (inclusive) or after it, forwards or backwards; with keyOnly, only the entries of the probe's key.
    // Returns how many it copied; fewer than the span holds means the end of the index or of the key.
    int Read(in TProbe from, bool inclusive, bool forward, bool keyOnly, Span<TEntry> into);

    // Writer, while no reader runs: checks every invariant of the structure and returns the entries in
    // order; InvalidOperationException for a broken one.
    List<TEntry> Validate();

    // Writer, while no reader runs: frees everything retired.
    void DrainAll();
}
