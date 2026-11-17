using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Replication;

// The body of Hello and of HelloReply: what a node is and what it holds (brief of Epic 1, sprint S10,
// with the planner's blocks of 15 and 17 November 2026). An empty child sends an empty database identity,
// history zero and an empty schema fingerprint: it has no schema before it loads one. WireFormat,
// LayoutFormat and CaseTableVersion are those of the sender (Protocol, Schema.CaseTable); HelloCheck says
// what a master and a child require of each other.
internal readonly record struct HelloBody(
    Guid DatabaseId,
    ulong History,
    LatticeFingerprint SchemaFingerprint,
    int WireFormat,
    int LayoutFormat,
    int CaseTableVersion,
    LatticeRoleEnum Role);
