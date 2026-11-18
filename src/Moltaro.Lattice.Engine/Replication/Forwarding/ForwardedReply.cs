using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Replication.Forwarding;

// What the body of a reply says (ForwardEncoding.ReadReply): the result of a forwarded write, or the
// refusal of the master as the exception the caller of the child is given, equal in its error, message,
// Limit, OperationIndex and CommittedPosition. Exactly one of the two is set.
internal readonly record struct ForwardedReply(LatticeWriteResult? Result, LatticeException? Refusal);
