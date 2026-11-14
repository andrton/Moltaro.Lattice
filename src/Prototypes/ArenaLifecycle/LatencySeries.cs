using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed class LatencySeries
{
    private readonly string _name;
    private readonly long[] _ticks;
    private int _count;

    public LatencySeries(string name, int capacity)
    {
        _name = name;
        _ticks = new long[capacity];
    }

    // One owning thread per series. Pacing between operations is not timed.
    public void Add(long started) => _ticks[_count++] = Stopwatch.GetTimestamp() - started;

    public LatencyResult Result()
    {
        Check.That(_count > 0, "Empty latency sample.");
        var values = _ticks[.._count];
        Array.Sort(values);
        var factor = 1_000_000.0 / Stopwatch.Frequency;
        double Percentile(double fraction) => values[Math.Max(0, (int)Math.Ceiling(_count * fraction) - 1)] * factor;
        return new LatencyResult(_name, _count, values.Average() * factor,
            Percentile(0.5), Percentile(0.95), Percentile(0.99), values[^1] * factor);
    }
}
