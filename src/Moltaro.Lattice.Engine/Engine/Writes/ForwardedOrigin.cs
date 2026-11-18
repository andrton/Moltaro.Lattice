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

    // Outside the lock of the writes, before the write leaves the barrier of the close: records the
    // refusal of the write as its reply (WriteCoordinator, "Forwarded writes"). A LatticeException as it
    // is; the close of the master, which refused the write before it was executed, as Unavailable, not
    // executed; and any other exception, thrown before the dispatch for a defect of the child's
    // validation, as Unavailable with its message (the planner's decision after the study, point 5).
    public void Refuse(Exception exception) => Replies.Refuse(CorrelationId, RefusalOf(exception));

    private static LatticeException RefusalOf(Exception exception) => exception switch
    {
        LatticeException refusal => refusal,
        ObjectDisposedException => new LatticeException(LatticeErrorEnum.Unavailable, "The master was closing and did not execute the write."),
        _ => new LatticeException(LatticeErrorEnum.Unavailable, exception.Message),
    };
}
