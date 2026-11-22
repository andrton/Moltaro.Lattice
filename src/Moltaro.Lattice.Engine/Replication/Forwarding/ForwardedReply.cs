using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Replication.Forwarding;

// What the body of a reply says (ForwardEncoding.ReadReply): the result of a forwarded write, or the
// refusal of the master as the exception the caller of the child is given, equal in its error, message,
// Limit, OperationIndex and CommittedPosition, and in IndexName once the identity of the unique index a
// refusal names is resolved against the schema of the child (ForwardEncoding.NameIndex). Exactly one of
// the two is set; IndexId only with the refusal of a value of a unique index.
internal readonly record struct ForwardedReply(LatticeWriteResult? Result, LatticeException? Refusal, int? IndexId);
