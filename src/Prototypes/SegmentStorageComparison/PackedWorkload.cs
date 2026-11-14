using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.SegmentStorageComparison;

// Both backends run these exact access loops over the same 32-byte format.
internal sealed class PackedWorkload(ISegmentStore store, int[] positions)
{
    public const int RecordBytes = 32;
    private readonly int _recordsPerSegment = store.SegmentBytes / RecordBytes;

    public int RecordCount => store.SegmentCount * _recordsPerSegment;

    #region Initialization and verification

    public void Initialize()
    {
        for (var segmentIndex = 0; segmentIndex < store.SegmentCount; segmentIndex++)
        {
            var segment = store.GetSpan(segmentIndex);
            for (var localIndex = 0; localIndex < _recordsPerSegment; localIndex++)
            {
                var id = segmentIndex * _recordsPerSegment + localIndex;
                var row = segment.Slice(localIndex * RecordBytes, RecordBytes);
                BinaryPrimitives.WriteInt64LittleEndian(row, id);
                BinaryPrimitives.WriteInt64LittleEndian(row[8..], id % 1000);
                BinaryPrimitives.WriteInt64LittleEndian(row[16..], id * 3L);
                BinaryPrimitives.WriteInt64LittleEndian(row[24..], id & 7);
            }
        }
    }

    public long Verify(HashSet<int>? changed)
    {
        long projection = 0;
        for (var segmentIndex = 0; segmentIndex < store.SegmentCount; segmentIndex++)
        {
            var segment = store.GetSpan(segmentIndex);
            for (var localIndex = 0; localIndex < _recordsPerSegment; localIndex++)
            {
                var id = segmentIndex * _recordsPerSegment + localIndex;
                var isChanged = changed?.Contains(id) == true;
                var value = isChanged ? (id ^ 17) % 1000 : id % 1000;
                var version = isChanged ? 17L : id * 3L;
                var row = segment.Slice(localIndex * RecordBytes, RecordBytes);
                if (BinaryPrimitives.ReadInt64LittleEndian(row) != id
                    || BinaryPrimitives.ReadInt64LittleEndian(row[8..]) != value
                    || BinaryPrimitives.ReadInt64LittleEndian(row[16..]) != version
                    || BinaryPrimitives.ReadInt64LittleEndian(row[24..]) != (id & 7))
                {
                    throw new InvalidOperationException($"Record {id} has unexpected contents.");
                }

                projection += id + value;
            }
        }

        return projection;
    }

    #endregion

    #region Measured operations

    public long ReadRandom()
    {
        long checksum = 0;
        foreach (var id in positions)
        {
            var row = GetRecord(id);
            checksum += BinaryPrimitives.ReadInt64LittleEndian(row)
                + BinaryPrimitives.ReadInt64LittleEndian(row[8..])
                + BinaryPrimitives.ReadInt64LittleEndian(row[16..])
                + BinaryPrimitives.ReadInt64LittleEndian(row[24..]);
        }

        return checksum;
    }

    public long ScanProjection()
    {
        long checksum = 0;
        for (var segmentIndex = 0; segmentIndex < store.SegmentCount; segmentIndex++)
        {
            var segment = store.GetSpan(segmentIndex);
            for (var offset = 0; offset < segment.Length; offset += RecordBytes)
            {
                var row = segment.Slice(offset, RecordBytes);
                checksum += BinaryPrimitives.ReadInt64LittleEndian(row)
                    + BinaryPrimitives.ReadInt64LittleEndian(row[8..]);
            }
        }

        return checksum;
    }

    public long UpdateRandom()
    {
        long checksum = 0;
        foreach (var id in positions)
        {
            var row = GetRecord(id);
            var value = (id ^ 17) % 1000;
            BinaryPrimitives.WriteInt64LittleEndian(row[8..], value);
            BinaryPrimitives.WriteInt64LittleEndian(row[16..], 17);
            checksum += value;
        }

        return checksum;
    }

    private Span<byte> GetRecord(int id)
    {
        var segment = store.GetSpan(id / _recordsPerSegment);
        return segment.Slice(id % _recordsPerSegment * RecordBytes, RecordBytes);
    }

    #endregion
}
