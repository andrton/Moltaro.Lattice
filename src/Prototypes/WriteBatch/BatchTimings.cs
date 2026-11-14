using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed class BatchTimings(int size)
{
    private readonly List<(BatchObservation Observation, long Elapsed)> _samples = new(12);

    public void Add(BatchObservation observation, long elapsed) => _samples.Add((observation, elapsed));

    public BatchMeasurement Report()
    {
        return new BatchMeasurement(size,
            Summarize("Complete WriteBatch including SQL/receipt work", _samples.Select(value => value.Elapsed)),
            Summarize("Prepare arena/index plans", _samples.Select(value => value.Observation.PreparationTicks)),
            Summarize("Journal transaction through commit", _samples.Select(value => value.Observation.StorageTicks)),
            Summarize("Wait for publication barrier", _samples.Select(value => value.Observation.PublicationWaitTicks)),
            Summarize("Hold publication barrier", _samples.Select(value => value.Observation.PublicationHoldTicks)),
            _samples.Max(value => value.Observation.PublicationAllocatedBytes),
            _samples.Max(value => value.Observation.PreparedNativeCapacity),
            _samples.Max(value => value.Observation.NativeCapacityPrepared - value.Observation.NativeCapacityBefore));
    }

    public static LatencyResult Summarize(string name, IEnumerable<long> source)
    {
        var samples = source.Order().ToArray();
        Check.That(samples.Length > 0, "No batch measurement samples.");
        var factor = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        double Percentile(double fraction) => samples[(int)Math.Ceiling(samples.Length * fraction) - 1] * factor;
        return new LatencyResult(name, samples.Length, samples.Average() * factor,
            Percentile(.5), Percentile(.95), Percentile(.99), samples[^1] * factor);
    }
}
