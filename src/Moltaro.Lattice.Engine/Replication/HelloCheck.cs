using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Replication;

// What a master and a child require of each other's Hello before any data is sent (brief of Epic 1,
// sprint S10, Acceptance; the planner's blocks of 15 November 2026, "Identity", "Byte order" and "The
// version of the case table", and of 17 November, point 1). The handshake checks compatibility, not
// identity: it authenticates nobody (milestone contract, "Replication transport and initial loading").
//
// The checks, in this order, the first that fails giving the reason:
// - Format: the wire format, the layout format with its byte order, and the version of the case table
//   are this node's. Two nodes whose case tables differ would hold different keys equal; two whose
//   layouts or byte orders differ would read each other's roots wrongly. They are compared for equality
//   only; nothing is converted.
// - Role: a master accepts only a child, and a child only a master.
// - Database, History, Schema: a child that holds nothing sends an empty identity, history zero and an
//   empty fingerprint, and holds no data of another database; a child that holds a database has to hold
//   the master's, in the master's history, with the master's schema. In S10 every child is empty, so a
//   refusal for these comes only from a Hello a test makes; reconnect and reload (S34) send what the
//   child holds. A child requires of its master that it names a database, a history and a schema.
internal static class HelloCheck
{
    // The Hello of a node of this process: its identity and what it holds, with the formats of this
    // process.
    public static HelloBody Of(Guid databaseId, ulong history, LatticeFingerprint schemaFingerprint, LatticeRoleEnum role) =>
        new(databaseId, history, schemaFingerprint, Protocol.WireFormat, Protocol.LayoutFormat, CaseTable.Version, role);

    // Why the master whose Hello is given refuses the peer, or null when it accepts it.
    public static RefuseReasonEnum? RefusalOfChild(in HelloBody master, in HelloBody child)
    {
        if (!SameFormats(master, child))
        {
            return RefuseReasonEnum.Format;
        }

        if (child.Role != LatticeRoleEnum.Child)
        {
            return RefuseReasonEnum.Role;
        }

        if (child.DatabaseId != Guid.Empty && child.DatabaseId != master.DatabaseId)
        {
            return RefuseReasonEnum.Database;
        }

        if (child.History != 0 && child.History != master.History)
        {
            return RefuseReasonEnum.History;
        }

        if (child.SchemaFingerprint != default && child.SchemaFingerprint != master.SchemaFingerprint)
        {
            return RefuseReasonEnum.Schema;
        }

        return null;
    }

    // Why the child whose Hello is given cannot load from the peer that replied, for the message of its
    // failed open, or null when it can.
    public static string? RefusalOfMaster(in HelloBody child, in HelloBody master)
    {
        if (!SameFormats(child, master))
        {
            return $"The master speaks the wire format {master.WireFormat}, stores roots in the layout format 0x{master.LayoutFormat:X8} and compares text by the case table {master.CaseTableVersion}; this child speaks {child.WireFormat}, 0x{child.LayoutFormat:X8} and {child.CaseTableVersion}.";
        }

        if (master.Role != LatticeRoleEnum.Master)
        {
            return $"The peer at the address of the master replied as {master.Role}, not as a master.";
        }

        if (master.DatabaseId == Guid.Empty || master.History == 0 || master.SchemaFingerprint == default)
        {
            return "The master named no database, history or schema in its reply.";
        }

        return null;
    }

    private static bool SameFormats(in HelloBody first, in HelloBody second) =>
        first.WireFormat == second.WireFormat && first.LayoutFormat == second.LayoutFormat && first.CaseTableVersion == second.CaseTableVersion;
}
