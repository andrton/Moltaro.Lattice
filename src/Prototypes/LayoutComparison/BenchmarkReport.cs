namespace Moltaro.Lattice.Prototypes.LayoutComparison;

internal sealed record BenchmarkReport(
    string Layout,
    int Rows,
    int Positions,
    int Seed,
    string Runtime,
    string OperatingSystem,
    string Architecture,
    int LogicalProcessors,
    string? TieredCompilation,
    long PayloadBytes,
    long ApproximateRetainedManagedBytes,
    bool CorrectnessPassed,
    IReadOnlyList<BenchmarkMeasurement> Measurements);
