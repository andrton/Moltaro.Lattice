namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

// Alternative rules exist only as deliberately broken negative controls.
internal enum ReplayRuleEnum
{
    PerRoot,
    Unconditional,
    MaximumBaseRevision,
    IgnoreDeletionWatermarks
}
