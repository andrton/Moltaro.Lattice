namespace Moltaro.Lattice.Prototypes.LayoutComparison;

internal sealed class ColumnLayout : ILayout
{
    #region Storage

    private readonly long[] _ids;
    private readonly double[] _values;
    private readonly double[] _weights;
    private readonly long[] _timestamps;
    private readonly int[] _groups;
    private readonly int[] _flags;
    private readonly long[] _tagsA;
    private readonly long[] _tagsB;
    private readonly long[] _tagsC;

    #endregion

    public ColumnLayout(int count)
    {
        _ids = new long[count];
        _values = new double[count];
        _weights = new double[count];
        _timestamps = new long[count];
        _groups = new int[count];
        _flags = new int[count];
        _tagsA = new long[count];
        _tagsB = new long[count];
        _tagsC = new long[count];

        for (var index = 0; index < count; index++)
        {
            var row = SampleRow.Create(index);
            _ids[index] = row.Id;
            _values[index] = row.Value;
            _weights[index] = row.Weight;
            _timestamps[index] = row.Timestamp;
            _groups[index] = row.GroupId;
            _flags[index] = row.Flags;
            _tagsA[index] = row.TagA;
            _tagsB[index] = row.TagB;
            _tagsC[index] = row.TagC;
        }
    }

    public long PayloadBytes => (long)_ids.Length * 64;

    public long ReadRecords(int[] positions)
    {
        long checksum = 0;
        foreach (var position in positions)
        {
            checksum += ReadRecord(position).Checksum();
        }

        return checksum;
    }

    public long ScanProjection()
    {
        long checksum = 0;
        for (var index = 0; index < _ids.Length; index++)
        {
            if (_groups[index] == 7 && _values[index] >= 500)
            {
                checksum += _ids[index] + (long)_values[index];
            }
        }

        return checksum;
    }

    public long UpdateFields(int[] positions)
    {
        long checksum = 0;
        foreach (var position in positions)
        {
            _values[position] = (position ^ 17) % 1000;
            _timestamps[position] = 17;
            checksum += (long)_values[position];
        }

        return checksum;
    }

    public long Checksum()
    {
        long checksum = 0;
        for (var index = 0; index < _ids.Length; index++)
        {
            checksum += ReadRecord(index).Checksum();
        }

        return checksum;
    }

    private SampleRow ReadRecord(int index) => new()
    {
        Id = _ids[index],
        Value = _values[index],
        Weight = _weights[index],
        Timestamp = _timestamps[index],
        GroupId = _groups[index],
        Flags = _flags[index],
        TagA = _tagsA[index],
        TagB = _tagsB[index],
        TagC = _tagsC[index]
    };
}
