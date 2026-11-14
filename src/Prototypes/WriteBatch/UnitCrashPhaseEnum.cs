namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal enum UnitCrashPhaseEnum
{
    ForegroundBeforeCommit,
    ForegroundAfterCommit,
    ForegroundMemberPublished,
    ForegroundAfterPublication,
    UnitsWritten,
    CursorWritten,
    AppliedCommitted,
    CleanupDeleted,
    CleanupCommitted
}
