namespace Moltaro.Lattice.Measurements.Scenarios;

// The distribution of the times of one series of ReadSeries, in ticks of the timer, for a scenario that
// reports the median of several rounds of a series beside each round.
internal readonly record struct SeriesFigures(long P50, long P95, long P99, long Max);
