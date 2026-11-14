namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal sealed record SlowOperation(TailSample Sample, GateTiming[] Gates, StageTiming[] Stages);
