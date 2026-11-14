using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.LayoutComparison;

internal sealed class RowLayout : ILayout
{
    private readonly SampleRow[] _rows;

    public RowLayout(int count)
    {
        _rows = new SampleRow[count];
        for (var index = 0; index < count; index++)
        {
            _rows[index] = SampleRow.Create(index);
        }
    }

    public long PayloadBytes => (long)_rows.Length * Unsafe.SizeOf<SampleRow>();

    public long ReadRecords(int[] positions)
    {
        long checksum = 0;
        foreach (var position in positions)
        {
            checksum += _rows[position].Checksum();
        }

        return checksum;
    }

    public long ScanProjection()
    {
        long checksum = 0;
        foreach (ref readonly var row in _rows.AsSpan())
        {
            if (row.GroupId == 7 && row.Value >= 500)
            {
                checksum += row.Id + (long)row.Value;
            }
        }

        return checksum;
    }

    public long UpdateFields(int[] positions)
    {
        long checksum = 0;
        foreach (var position in positions)
        {
            ref var row = ref _rows[position];
            row.Value = (position ^ 17) % 1000;
            row.Timestamp = 17;
            checksum += (long)row.Value;
        }

        return checksum;
    }

    public long Checksum()
    {
        long checksum = 0;
        foreach (ref readonly var row in _rows.AsSpan())
        {
            checksum += row.Checksum();
        }

        return checksum;
    }
}
