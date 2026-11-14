using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.ReplicaStreamComparison;

internal sealed class MemoryImage
{
    public const int SegmentBytes = 4 * 1024 * 1024;

    public MemoryImage(int sizeMiB)
    {
        Segments = new byte[sizeMiB / 4][];
    }

    public byte[][] Segments { get; }

    public static MemoryImage CreateSource(int sizeMiB)
    {
        var image = new MemoryImage(sizeMiB);
        for (var index = 0; index < image.Segments.Length; index++)
        {
            var segment = new byte[SegmentBytes];
            segment.AsSpan().Fill((byte)(index % 251));
            BinaryPrimitives.WriteInt64LittleEndian(segment, index);
            BinaryPrimitives.WriteInt64LittleEndian(segment.AsSpan(SegmentBytes - sizeof(long)), index);
            image.Segments[index] = segment;
        }
        return image;
    }

    public void Verify()
    {
        for (var index = 0; index < Segments.Length; index++)
        {
            var segment = Segments[index].AsSpan();
            var first = BinaryPrimitives.ReadInt64LittleEndian(segment);
            var last = BinaryPrimitives.ReadInt64LittleEndian(segment[^sizeof(long)..]);
            var interior = segment.Slice(sizeof(long), SegmentBytes - 2 * sizeof(long));
            if (first != index || last != index || interior.IndexOfAnyExcept((byte)(index % 251)) >= 0)
            {
                throw new InvalidOperationException($"Transferred segment {index} differs from its expected contents.");
            }
        }
    }
}
