using System.Buffers;
using Moltaro.Lattice.Engine.Delivery;

namespace Moltaro.Lattice.Engine.Writes;

// Where a write a child forwarded to this master came from, and what it brings in place of values (brief
// of Epic 1, sprint S12, the planner's decision after the study, points 1 and 2; Children.ChildConnection):
// the replies of the child's connection and the correlation id of the request, so that the hold that
// publishes the write records its reply before its transaction is appended to the tail; and the root of an
// insert, a replace or an upsert as the child sent it, in the load format, its last mutation 0 — empty for
// a delete. The receiver has checked the root as a child checks a root of a load, and that its key is the
// key of the request; the write path places it when it prepares the write, with the sequence it publishes
// at as its last mutation. The root is the request's own memory and lives as long as this record.
internal sealed class ForwardedOrigin
{
    public ForwardedOrigin(RecordedReplies replies, ulong correlationId, ReadOnlySequence<byte> root)
    {
        Replies = replies;
        CorrelationId = correlationId;
        Root = root;
    }

    public RecordedReplies Replies { get; }

    public ulong CorrelationId { get; }

    // The root after its type id and its last mutation sequence: its layout version, its root payload and
    // its values out of line (Replication.Load.BlockImport); empty for a delete.
    public ReadOnlySequence<byte> Root { get; }
}
