namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record WorkerResult(string Group, string Runtime, string Architecture,
    int RuntimeProcessors, long AffinityMask, double WallMilliseconds, double CpuMilliseconds,
    long PeakWorkingSetBytes, long OutstandingNativeBytes, long StopwatchFrequency, long BaselineManagedHeapBytes,
    ExperimentResult Experiment);
