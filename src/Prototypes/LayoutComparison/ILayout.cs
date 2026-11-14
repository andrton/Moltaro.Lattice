namespace Moltaro.Lattice.Prototypes.LayoutComparison;

internal interface ILayout
{
    long PayloadBytes { get; }
    long ReadRecords(int[] positions);
    long ScanProjection();
    long UpdateFields(int[] positions);
    long Checksum();
}
