using System.Buffers;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Writes;

// Where a write a child forwarded to this master came from, and what it brings in place of values
// (Children.ChildConnection): the replies of the child's connection and the correlation id of the
// request, so that the hold that publishes the write records its reply before its transaction is
// appended to the tail (Delivery.RecordedReplies, "The order that matters"); and the root of an insert,
// a replace or an upsert as the child sent it, in the load format, its last mutation 0 — empty for a
// delete and for a write batch, whose members bring their roots (BatchMember). The receiver has checked the
// root as a child checks a root of a load, and that its key is the key of the request; the write path places
// it when it prepares the write, with the sequence it publishes at as its last mutation. The root is the
// request's own memory and lives as long as this record.
//
// The identity of a unique index a refusal names, which the reply carries in place of the name
// (Replication.Forwarding.ForwardEncoding), is that of the index within one entity type: of the write, or for
// a batch of the member the refusal names in LatticeException.OperationIndex, which a refusal of a batch for a
// unique index always sets (WriteCoordinator.Violation). So the record of a batch keeps the entity type of each
// member, and not the members, which the record of the write lets go of, with their roots, before the refusal
// is recorded (PendingWrite.LetGoOfInputs).
internal sealed class ForwardedOrigin
{
    // The entity type of one write; null for a batch.
    private readonly LatticeEntityType? _entityType;

    // The entity types of the members of a batch, in their order; null for one write.
    private readonly LatticeEntityType[]? _memberTypes;

    public ForwardedOrigin(RecordedReplies replies, ulong correlationId, ReadOnlySequence<byte> root, LatticeEntityType entityType)
    {
        Replies = replies;
        CorrelationId = correlationId;
        Root = root;
        _entityType = entityType;
    }

    // The origin of a write batch, with the entity types of its members, in their order.
    public ForwardedOrigin(RecordedReplies replies, ulong correlationId, LatticeEntityType[] memberTypes)
    {
        Replies = replies;
        CorrelationId = correlationId;
        Root = ReadOnlySequence<byte>.Empty;
        _memberTypes = memberTypes;
    }

    public RecordedReplies Replies { get; }

    public ulong CorrelationId { get; }

    // The root after its envelope: its layout version, its root payload and its values out of line
    // (Replication.Load.BlockImport); empty for a delete and for a batch.
    public ReadOnlySequence<byte> Root { get; }

    // Outside the lock of the writes, before the write leaves the barrier of the close: records the
    // refusal of the write as its reply (WriteCoordinator, "Forwarded writes"). A LatticeException as it
    // is; the close of the master, which refused the write before it was executed, as Unavailable, not
    // executed; and any other exception, thrown before the dispatch for a defect of the child's
    // validation, as Unavailable with its message.
    public void Refuse(Exception exception)
    {
        var refusal = RefusalOf(exception);

        Replies.Refuse(CorrelationId, refusal, refusal.IndexName is { } name && EntityTypeOf(refusal.OperationIndex).TryGetIndex(name, out var index) ? index.Id : null);
    }

    private static LatticeException RefusalOf(Exception exception) => exception switch
    {
        LatticeException refusal => refusal,
        ObjectDisposedException => new LatticeException(LatticeErrorEnum.Unavailable, "The master was closing and did not execute the write."),
        _ => new LatticeException(LatticeErrorEnum.Unavailable, exception.Message),
    };

    // The entity type a unique index of a refusal belongs to (see the class): of the write, or of the member
    // at the place given.
    private LatticeEntityType EntityTypeOf(int? operationIndex) => _memberTypes is { } members ? members[operationIndex!.Value] : _entityType!;
}
