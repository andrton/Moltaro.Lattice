namespace Moltaro.Lattice.Replication;

// Why a master refuses a peer in the handshake (brief of Epic 1, sprint S10; HelloCheck says which
// difference gives which reason).
internal enum RefuseReasonEnum : ushort
{
    // The peer holds another database.
    Database = 1,

    // The peer holds another history of the database.
    History = 2,

    // The peer holds another schema.
    Schema = 3,

    // The peer speaks another wire format, stores roots in another layout or byte order, or compares
    // text by another version of the case table.
    Format = 4,

    // The peer is not a child.
    Role = 5,
}
