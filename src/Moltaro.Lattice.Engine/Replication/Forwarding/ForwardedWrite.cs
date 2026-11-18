using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Replication.Forwarding;

// What the body of a forwarded write says before its key (ForwardEncoding.ReadRequest): the kind of the
// write, the entity type, the revision the entity must have and the position the write depends on, each
// null when the child gave none. The key and the root follow it in the body, read by the master with the
// entity type they belong to (Engine.Children).
internal readonly record struct ForwardedWrite(WriteKindEnum Kind, uint TypeId, LatticeRevision? ExpectedRevision, LatticePosition? MinimumPosition);
