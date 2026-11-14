namespace Moltaro.Lattice.Prototypes.SegmentStorageComparison;

// The experiment is single-threaded. No borrowed span may survive replacement
// or disposal; this interface is not a proposed database read-lifetime API.
internal interface ISegmentStore : IDisposable
{
    int SegmentCount { get; }

    int SegmentBytes { get; }

    Span<byte> GetSpan(int index);

    void ReplaceSegment(int index);
}
