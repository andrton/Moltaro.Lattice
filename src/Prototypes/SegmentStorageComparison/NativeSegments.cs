using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.SegmentStorageComparison;

internal sealed unsafe class NativeSegments : ISegmentStore
{
    private readonly nint[] _segments;
    private bool _disposed;

    public NativeSegments(int segmentCount, int segmentBytes)
    {
        SegmentBytes = segmentBytes;
        _segments = new nint[segmentCount];
        try
        {
            for (var index = 0; index < segmentCount; index++)
            {
                _segments[index] = (nint)NativeMemory.AllocZeroed((nuint)segmentBytes);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int SegmentCount => _segments.Length;

    public int SegmentBytes { get; }

    public Span<byte> GetSpan(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<byte>((void*)_segments[index], SegmentBytes);
    }

    public void ReplaceSegment(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var previous = _segments[index];
        var replacement = NativeMemory.AllocZeroed((nuint)SegmentBytes);
        new ReadOnlySpan<byte>((void*)previous, SegmentBytes)
            .CopyTo(new Span<byte>(replacement, SegmentBytes));
        _segments[index] = (nint)replacement;
        // No readers exist in this experiment; production must establish that
        // boundary before freeing storage or reusing addresses.
        NativeMemory.Free((void*)previous);
    }

    public void Dispose()
    {
        for (var index = 0; index < _segments.Length; index++)
        {
            NativeMemory.Free((void*)_segments[index]);
            _segments[index] = 0;
        }

        _disposed = true;
    }
}
