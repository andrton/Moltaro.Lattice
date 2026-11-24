using Moltaro.Lattice.Access;
using Moltaro.Lattice.Replication.Forwarding;

namespace Moltaro.Lattice.Engine.Delivery;

// One reply a master owes a child for a write it forwarded (RecordedReplies): the correlation id of its
// request, and the result of the write or the refusal, which is null for a result.
internal readonly record struct RecordedReply(ulong CorrelationId, LatticeWriteResult Result, LatticeException? Refusal, int? IndexId, UpdateBitmap Updates = default);
