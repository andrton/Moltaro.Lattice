namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal enum UnitApplyPhaseEnum
{
    PrefixRead,
    UnitsWritten,
    CursorWritten,
    AppliedCommitted,
    CleanupDeleted,
    CleanupCommitted
}
