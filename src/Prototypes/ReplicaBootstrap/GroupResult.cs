namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed record GroupResult(
    string Group,
    string Runtime,
    string Architecture,
    int RuntimeProcessors,
    double ElapsedMilliseconds,
    double CpuMilliseconds,
    long ProcessPeakWorkingSetBytes,
    int PeakLiveNativeBlockBytes,
    int RemainingNativeBlocks,
    List<ScenarioResult> Scenarios);
