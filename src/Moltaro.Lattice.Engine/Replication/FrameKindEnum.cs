namespace Moltaro.Lattice.Replication;

// What a frame of the replication transport carries (brief of Epic 1, sprint S10). No kind has the value
// zero, so a header of zeros names none.
internal enum FrameKindEnum : ushort
{
    // From a child to its master: who it is and what it holds (HelloBody).
    Hello = 1,

    // From a master to a child it accepted: the master's own HelloBody.
    HelloReply = 2,

    // A part of the schema of the master and of the counts of its roots (Load.SchemaTransfer).
    Schema = 3,

    // Roots of the database, or a part of one (Load.RootFramer).
    Blocks = 4,

    // The end of the load: where it ended and how many roots it sent (Load.LoadEndBody).
    LoadEnd = 5,

    // From a master to a peer it does not accept: the reason (RefuseReasonEnum). The master closes the
    // connection after it.
    Refuse = 6,

    // From a master to a child after LoadEnd: a committed transaction, or a part of one
    // (Transactions.TransactionFormat). 8 is not used: a transaction ends where its announced length
    // ends (the planner's decision of 17 November 2026 after the study of S11, point 6).
    Transaction = 7,

    // From a child to its master: the sequence up to which the child has applied all it has received
    // (Transactions.TransactionFormat).
    Applied = 9,
}
