namespace Moltaro.Lattice.Replication.Transactions;

// What one effect of a committed transaction does to one entity (brief of Epic 1, sprint S11; the
// planner's decision of 17 November 2026 after the study, point 6). Its value is the first byte of the
// effect (TransactionFormat). No kind has the value zero.
internal enum EffectKindEnum : byte
{
    // The entity has the state of the root the effect carries: an insert, a replace, an upsert.
    Write = 1,

    // The entity of the key the effect carries is deleted.
    Delete = 2,

    // A write that changed nothing, a delete of an absent key: only its position (the owner's answer to
    // Q39).
    Unchanged = 3,
}
