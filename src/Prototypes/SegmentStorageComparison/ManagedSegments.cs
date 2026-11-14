namespace Moltaro.Lattice.Prototypes.SegmentStorageComparison;

internal sealed class ManagedSegments : ISegmentStore
{
    private readonly byte[][] _segments;
    private bool _disposed;

    public ManagedSegments(int segmentCount, int segmentBytes)
    {
        SegmentBytes = segmentBytes;
        _segments = new byte[segmentCount][];
        for (var index = 0; index < segmentCount; index++)
        {
            _segments[index] = new byte[segmentBytes];
        }
    }

    public int SegmentCount => _segments.Length;

    public int SegmentBytes { get; }

    public Span<byte> GetSpan(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _segments[index];
    }

    public void ReplaceSegment(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _segments[index];
        var replacement = new byte[SegmentBytes];
        previous.AsSpan().CopyTo(replacement);
        _segments[index] = replacement;
        // The old array is now eligible for GC; no explicit GC occurs here.
    }

    public void Dispose()
    {
        Array.Clear(_segments);
        _disposed = true;
    }
}
