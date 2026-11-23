using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Child.Forwarding;

// The writes of a child: each is forwarded to its master over the one connection of the child, and the
// call returns once the master has executed it and this child has applied it
// (docs/decisions/replication-and-hosting.md, "Completion of a write forwarded through a child";
// docs/decisions/session-consistency.md).
//
// A write through a child, end to end, across the two nodes:
// 1. The set of the child (Sets.SetOperations) calls WriteAsync, which checks the write, encodes its
//    request and queues it (steps 1 to 3 of a call, below).
// 2. The sender of the child's connection (ChildSender) registers the call (PendingRequests) and sends
//    the frames of the request (Child.MasterLink).
// 3. On the master, the receiver of the connection (Children.ChildConnection, Children.RequestReceiver)
//    puts the request together (Replication.Forwarding.MessageAssembler), checks it and its root
//    (Replication.Load.BlockImport) and submits it to the write path (Writes.WriteCoordinator.Forward).
// 4. The write path prepares it, placing the root it brought (Writes.WriteExecutor), and publishes it as
//    any write of the master; its reply is recorded for the connection (Delivery.RecordedReplies) before
//    its transaction is appended to the tail of the children.
// 5. The sender of that connection sends the reply, then the transaction.
// 6. On the child, the follower hands the reply to the table of the pending requests, which completes the
//    call (Following.Follower, PendingRequests), and applies the transaction (Loading.ReceivedChanges).
// 7. WriteAsync waits until the clock of the child has the position of the write, moves the session to
//    it, and returns (step 4 of a call).
//
// A call, in its order:
// 1. The checks that need no master. The set has checked the arguments and validated the entity against
//    the schema, before it calls. An entity type the engine does not store is NotSupportedException;
//    the session is bound to the history of the child, which is its master's, here, before anything is
//    queued (Positions.PositionClock.Bind): an empty one is bound, and one of another history, also one
//    another database bound first, is HistoryMismatch, since no node can order a write after it
//    (docs/decisions/session-consistency.md, "Facade handoff"); the master checks it again. So the session
//    of a write the master committed always moves to its position (step 4). A token cancelled already ends
//    the call. The child does not wait for the position of the session: the master orders the write after
//    it (docs/decisions/session-consistency.md, "Facade handoff").
// 2. The request (Replication.Forwarding.ForwardEncoding): the kind, the entity type, the expected
//    revision, the position of the session, the key, and for an insert, a replace or an upsert the root of
//    the entity in the load format, which the root writer writes without the arena of the child
//    (Storage.Roots.RootWriter, "A root for a forwarded write"). No timeout travels.
// 3. The queue of the sender of the connection (ChildSender), unless the child has no connection to its
//    master: then Unavailable, not executed. The sender is looked for before the request is encoded, and a
//    call it refuses to queue leaves no registration on the token, so that a refused write keeps nothing
//    reachable. Until the sender takes it, the token withdraws the call; then the call waits for the real
//    outcome.
// 4. The reply. A refusal of the master is thrown as the master gave it. A result is waited for until this
//    child has applied its position, on the clock of the child, bounded by the SynchronizationTimeout of
//    the session when it sets one, otherwise by the option; then the session moves to the position, and
//    the call returns. The reply comes before the transaction of the write (Children.ChildConnection), so
//    the wait begins before the child can have applied it.
//
// A write batch is one call (WriteBatchAsync): its request carries its members in their order, each with the
// root the batch of the core encoded when it was added, after the head that carries the position of the
// session (Replication.Forwarding.ForwardEncoding); the rest of the call is that of one write. The child
// applies the transaction of the batch at its one position (Loading.ReceivedChanges), so once its clock has
// that position every member is visible, and it acknowledges no part of it.
//
// The wait for the application ends otherwise: at the timeout, or at once when the child lost its master
// or faulted, in SynchronizationTimeout with the committed position, since the write is committed and must
// not be repeated, and an application retries on Unavailable; at the close of the child, with the result,
// since a closed child serves no read. The session moves to the committed position in each of these.
//
// What the end of the connection does to a call is the sender's and the table's (ChildSender,
// PendingRequests): not sent whole, Unavailable; sent whole and not answered, IndeterminateOutcome, unless
// the master sent the frame Closed before the end, which ends every call it did not answer in
// Unavailable, not executed (Following.Follower, "The frame Closed").
//
// The waits, and how each ends. The wait for the reply has no timeout and no token, once the sender has
// taken the call, as a write of a master after its dispatch: it ends with the reply, with the end of the
// connection, and with the close of the child, which ends the connection; a master that vanishes without
// ending the connection keeps it waiting, since reconnection is not built yet (exception X6). The wait for
// the application has the timeout of the call and ends as the class says.
internal sealed class Forwarder
{
    private readonly PositionClock _clock;

    // How long a write waits for its application when its session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    // The sender of the connection while the child follows its master; null before and once it has lost
    // it (Following.Follower).
    private volatile ChildSender? _sender;

    public Forwarder(PositionClock clock, TimeSpan synchronizationTimeout)
    {
        _clock = clock;
        _synchronizationTimeout = synchronizationTimeout;
    }

    // The child follows its master, and its writes go through the sender of the connection.
    public void Attach(ChildSender sender) => _sender = sender;

    // The child has lost its master: every write from now on is Unavailable, and a wait for a position it
    // has not reached ends at once (PositionClock.Lose). Called before the child ends its side of the
    // connection, so that the master's close, which waits for that, finds the child marked.
    public void LoseMaster()
    {
        _sender = null;
        _clock.Lose();
    }

    // A write of the child (see the class). The values are those of a complete entity for an insert, a
    // replace and an upsert, and null for a delete. Every refusal comes through the task.
    public ValueTask<LatticeWriteResult> WriteAsync(
        EntityTable table,
        WriteKindEnum kind,
        EntityKey key,
        object?[]? values,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken cancellationToken)
    {
        ChildSender sender;
        ForwardedCall call;

        try
        {
            ThrowIfNotStored(table);
            sender = Begin(session, cancellationToken);
            call = new ForwardedCall(Request(table, kind, key, values, expectedRevision, session?.MinPosition), table.EntityType);
        }
        catch (Exception exception)
        {
            return Refused(exception, cancellationToken);
        }

        return ForwardAsync(sender, call, session, cancellationToken);
    }

    // A write batch of the child (see the class): its members, in their order, as one request, which its master
    // executes as one write (Writes.WriteCoordinator, "A write batch"), with the roots the batch of the core
    // encoded when they were added. The members are not kept once the request is written, so their roots are
    // held only until then. Every refusal comes through the task.
    public ValueTask<LatticeWriteResult> WriteBatchAsync(BatchMember[] members, LatticeSession? session, CancellationToken cancellationToken)
    {
        ChildSender sender;
        ForwardedCall call;

        try
        {
            foreach (var member in members)
            {
                ThrowIfNotStored(member.Table);
            }

            sender = Begin(session, cancellationToken);
            call = new ForwardedCall(BatchRequest(members, session?.MinPosition), EntityTypesOf(members));
        }
        catch (Exception exception)
        {
            return Refused(exception, cancellationToken);
        }

        return ForwardAsync(sender, call, session, cancellationToken);
    }

    private static LatticeException NoMaster() =>
        new(LatticeErrorEnum.Unavailable, "This child has no connection to its master: the write was not executed.");

    private static void ThrowIfNotStored(EntityTable table)
    {
        if (table.Layout is null)
        {
            throw new NotSupportedException(table.Refusal);
        }
    }

    // A refusal before anything is queued, as the task of the call: cancelled for its token, failed otherwise.
    private static ValueTask<LatticeWriteResult> Refused(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && cancellationToken.IsCancellationRequested
            ? ValueTask.FromCanceled<LatticeWriteResult>(cancellationToken)
            : ValueTask.FromException<LatticeWriteResult>(exception);

    // The checks of step 1 of a call, after those of its entity types (see the class): the token, the close, the
    // session bound before anything is queued, and the sender of the connection, which is looked for before the
    // request is encoded.
    private ChildSender Begin(LatticeSession? session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _clock.ThrowIfClosed();

        if (session is not null)
        {
            _clock.Bind(session);
        }

        return _sender ?? throw NoMaster();
    }

    // Steps 3 and 4 of a call (see the class): the queue of the sender, the reply, and the wait until this child
    // has applied the position of a write its master committed.
    private async ValueTask<LatticeWriteResult> ForwardAsync(ChildSender sender, ForwardedCall call, LatticeSession? session, CancellationToken cancellationToken)
    {
        call.WithdrawOn(cancellationToken);

        // The sender ended since it was read: the call ends here, and its registration with it.
        if (!sender.TryQueue(call))
        {
            call.TryEnd();

            throw NoMaster();
        }

        var reply = await call.Outcome.ConfigureAwait(false);

        if (reply.Refusal is { } refusal)
        {
            throw refusal;
        }

        var result = reply.Result!.Value;

        try
        {
            await WaitForApplicationAsync(result.Position, session?.SynchronizationTimeout ?? _synchronizationTimeout).ConfigureAwait(false);
        }
        finally
        {
            // The write is committed whatever ended the wait: the session depends on it.
            session?.Advance(result.Position);
        }

        return result;
    }

    // The request of the write (see the class, step 2).
    private static OutgoingMessage Request(EntityTable table, WriteKindEnum kind, in EntityKey key, object?[]? values, LatticeRevision? expectedRevision, LatticePosition? minimumPosition)
    {
        var request = new OutgoingMessage();
        var layout = table.Layout!;

        ForwardEncoding.WriteRequestHead(request, kind, (uint)table.EntityType.Id, expectedRevision, minimumPosition);
        table.Keys.WriteKey(key, request);

        if (kind != WriteKindEnum.Delete)
        {
            var writer = new RootWriter(layout, new ExportedValues(layout.DescriptorCount));

            try
            {
                RootValues.Write(ref writer, table.EntityType, values!);
                ForwardEncoding.WriteRootLength(request, RootEnvelope.Size + writer.ExportLength);
                RootEnvelope.Write(request, (uint)table.EntityType.Id, lastMutation: 0, member: 0);
                writer.Export(request);
            }
            catch
            {
                writer.Abandon();
                throw;
            }
        }

        request.Complete();

        return request;
    }

    // The request of a write batch (Replication.Forwarding.ForwardEncoding): its head, then each member as the
    // part of a request before its minimum position, its key, and but for a delete the length of its root and
    // the root after its envelope, the last mutation and the member 0.
    private static OutgoingMessage BatchRequest(BatchMember[] members, LatticePosition? minimumPosition)
    {
        var request = new OutgoingMessage();

        ForwardEncoding.WriteBatchHead(request, minimumPosition, members.Length);

        foreach (ref readonly var member in members.AsSpan())
        {
            var table = member.Table;
            var typeId = (uint)table.EntityType.Id;

            ForwardEncoding.WriteMemberHead(request, member.Kind, typeId, member.ExpectedRevision);
            table.Keys.WriteKey(member.Key, request);

            if (member.Kind == WriteKindEnum.Delete)
            {
                continue;
            }

            ForwardEncoding.WriteRootLength(request, RootEnvelope.Size + member.Root.Length);
            RootEnvelope.Write(request, typeId, lastMutation: 0, member: 0);

            foreach (var piece in member.Root)
            {
                request.Write(piece.Span);
            }
        }

        request.Complete();

        return request;
    }

    // The entity types of the members, in their order, in which a refusal of the batch names its unique index.
    private static LatticeEntityType[] EntityTypesOf(BatchMember[] members)
    {
        var entityTypes = new LatticeEntityType[members.Length];

        for (var place = 0; place < members.Length; place++)
        {
            entityTypes[place] = members[place].Table.EntityType;
        }

        return entityTypes;
    }

    // Waits until this child has applied the position of a write its master committed (see the class).
    private async ValueTask WaitForApplicationAsync(LatticePosition committed, TimeSpan timeout)
    {
        try
        {
            await _clock.WaitAsync(committed, timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (_clock.IsClosed && exception is LatticeException or ObjectDisposedException)
        {
            // A closed child serves no read: the write has its result.
        }
        catch (LatticeException exception)
        {
            // The timeout, the loss of the master or a fault of the child: the position does not arrive
            // in time, or cannot arrive (see the class).
            throw new LatticeException(
                LatticeErrorEnum.SynchronizationTimeout,
                $"The write is committed by the master at position {committed}, but this child has not applied it: {exception.Message} It is not rolled back; do not submit it again. An operation that requires this position will see it.",
                exception)
            {
                CommittedPosition = committed,
            };
        }
    }
}
