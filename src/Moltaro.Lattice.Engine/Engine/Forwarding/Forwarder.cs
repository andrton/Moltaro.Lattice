using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Forwarding;

// The writes of a child: each is forwarded to its master over the one connection of the child, and the
// call returns once the master has executed it and this child has applied it (brief of Epic 1, sprint S12;
// decision Q08.1.2, "Completion of a write forwarded through a child"; session consistency).
//
// A call, in its order:
// 1. The checks that need no master. The set has checked the arguments and validated the entity against
//    the schema (Q25), before it calls. An entity type the engine does not store is NotSupportedException;
//    a position of another history in the session is HistoryMismatch, here, before anything is queued
//    (the planner's decision after the study, point 9); the master checks it again. A token cancelled
//    already ends the call. The child does not wait for the position of the session: the master orders
//    the write after it (session consistency, "Facade handoff", point 4).
// 2. The request (Replication.Forwarding.ForwardEncoding): the kind, the entity type, the expected
//    revision, the position of the session, the key, and for an insert, a replace or an upsert the root of
//    the entity in the load format, which the root writer writes without the arena of the child
//    (Storage.RootWriter, "A root for a forwarded write"; point 1). No timeout travels (point 9).
// 3. The queue of the sender of the connection (ChildSender), unless the child has no connection to its
//    master: then Unavailable, not executed. The sender is looked for before the request is encoded, and a
//    call it refuses to queue leaves no registration on the token, so that a refused write keeps nothing
//    reachable (decided at the review of S12, point 4). Until the sender takes it, the token withdraws the
//    call; then the call waits for the real outcome.
// 4. The reply. A refusal of the master is thrown as the master gave it. A result is waited for until this
//    child has applied its position, on the clock of the child, bounded by the SynchronizationTimeout of
//    the session when it sets one, otherwise by the option; then the session moves to the position, and
//    the call returns. The reply comes before the transaction of the write (Children.ChildConnection), so
//    the wait begins before the child can have applied it.
//
// The wait for the application ends otherwise: at the timeout, or at once when the child lost its master
// or faulted, in SynchronizationTimeout with the committed position, since the write is committed and must
// not be repeated, and an application retries on Unavailable (settled by the planner with the
// implementation of S12, point 1); at the close of the child, with the result, since a closed child serves
// no read (point 8). The session moves to the committed position in each of these.
//
// What the end of the connection does to a call is the sender's and the table's (ChildSender,
// PendingRequests): not sent whole, Unavailable; sent whole and not answered, IndeterminateOutcome, unless
// the master sent the frame Closed before the end, which ends every call it did not answer in
// Unavailable, not executed (Following.Follower, "The frame Closed").
//
// The waits, and how each ends. The wait for the reply has no timeout and no token, once the sender has
// taken the call, as a write of a master after its dispatch: it ends with the reply, with the end of the
// connection, and with the close of the child, which ends the connection; a master that vanishes without
// ending the connection keeps it waiting until S34 brings reconnection (exception X6). The wait for the
// application has the timeout of the call and ends as the class says.
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
    // replace and an upsert, and null for a delete.
    public async ValueTask<LatticeWriteResult> WriteAsync(
        EntityTable table,
        WriteKindEnum kind,
        EntityKey key,
        object?[]? values,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken cancellationToken)
    {
        if (table.Layout is null)
        {
            throw new NotSupportedException(table.Refusal);
        }

        cancellationToken.ThrowIfCancellationRequested();
        _clock.ThrowIfClosed();

        var minimumPosition = session?.MinPosition;

        if (minimumPosition is { } position)
        {
            _clock.CheckHistory(position);
        }

        if (_sender is not { } sender)
        {
            throw NoMaster();
        }

        var call = new ForwardedCall(Request(table, kind, key, values, expectedRevision, minimumPosition));

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

    private static LatticeException NoMaster() =>
        new(LatticeErrorEnum.Unavailable, "This child has no connection to its master: the write was not executed.");

    // The request of the write (see the class, step 2).
    private static OutgoingMessage Request(EntityTable table, WriteKindEnum kind, in EntityKey key, object?[]? values, LatticeRevision? expectedRevision, LatticePosition? minimumPosition)
    {
        var request = new OutgoingMessage();
        var layout = table.Layout!;

        ForwardEncoding.WriteRequestHead(request, kind, (uint)table.EntityType.Id, expectedRevision, minimumPosition);
        table.WriteKey(key, request);

        if (kind != WriteKindEnum.Delete)
        {
            var writer = new RootWriter(layout, new ExportedValues(layout.DescriptorCount));

            try
            {
                RootValues.Write(ref writer, table.EntityType, values!);
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
