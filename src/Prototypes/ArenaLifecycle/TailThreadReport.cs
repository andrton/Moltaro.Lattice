namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record TailThreadReport(string Name, TailOperationSummary[] Operations, SlowOperation[] Slowest);
