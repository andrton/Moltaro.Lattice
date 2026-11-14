using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.LayoutComparison;

/// <summary>A 64-byte synthetic record; both layouts contain exactly these fields.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SampleRow
{
    public long Id { get; set; }
    public double Value { get; set; }
    public double Weight { get; set; }
    public long Timestamp { get; set; }
    public int GroupId { get; set; }
    public int Flags { get; set; }
    public long TagA { get; set; }
    public long TagB { get; set; }
    public long TagC { get; set; }

    public static SampleRow Create(int index) => new()
    {
        Id = index,
        Value = index % 1000,
        Weight = index % 100,
        Timestamp = index * 3L,
        GroupId = index % 128,
        Flags = index % 4,
        TagA = index * 5L,
        TagB = index * 7L,
        TagC = index * 11L
    };

    public readonly long Checksum() =>
        Id + (long)Value + (long)Weight + Timestamp + GroupId + Flags + TagA + TagB + TagC;
}
