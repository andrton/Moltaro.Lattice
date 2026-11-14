using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record LatencySummary(int Samples, double P50Microseconds, double P95Microseconds,
    double P99Microseconds, double MaximumMicroseconds)
{
    public static LatencySummary FromTicks(IEnumerable<long> ticks)
    {
        var sorted = ticks.Order().ToArray();
        if (sorted.Length == 0)
        {
            return new LatencySummary(0, 0, 0, 0, 0);
        }

        double At(double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1]
            * 1_000_000.0 / Stopwatch.Frequency;

        return new LatencySummary(sorted.Length, At(0.5), At(0.95), At(0.99), At(1));
    }
}
