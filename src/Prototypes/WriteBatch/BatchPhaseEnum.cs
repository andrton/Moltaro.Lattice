namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal enum BatchPhaseEnum
{
    MemberPrepared,
    Prepared,
    BeforeCommit,
    AfterCommit,
    MemberPublished,
    AfterPublication
}
