namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record ExperimentResult(string Name, string[] Checks,
    LatencyResult[] Latencies, MemoryPoint[] Memory, long PhaseManagedAllocatedBytesIncludingHarness,
    Dictionary<string, long> Counters, TailThreadReport[]? TailDiagnostics = null);
