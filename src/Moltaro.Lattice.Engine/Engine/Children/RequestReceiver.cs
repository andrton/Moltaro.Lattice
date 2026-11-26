using System.Buffers;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Updates;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Children;

// What a master does with the writes one child forwards: it puts each request together from its frames,
// checks it, and submits it to its write path, in the order the requests arrive and without awaiting one
// before it submits the next (Writes.WriteCoordinator, "Forwarded writes"). Every frame of the child goes
// through the receiver of its connection (ChildConnection), and so do the requests, one at a time.
//
// What is checked, before anything of a request is used. The correlation id of each request is the one
// after the id of the request before, from 1: 0 stays the load and the stream, and an id given again would
// be one still in flight. The frames of a request (MessageAssembler). Its body (ForwardEncoding): a kind
// of write, no expected revision for an insert, an entity type the engine stores, a key of that type, and
// for an insert, a replace or an upsert a root of its entity type and a last mutation of 0, which passes
// the check a child makes of a root received in a load (Replication.Load.BlockImport.Check), and whose key
// is the key of the request, the text of a String key code unit for code unit. A write batch (ForwardEncoding,
// from the wire format 5) is checked member by member in the same way, and as a whole: a count of members its
// body can hold, no entity named by two members (Entities.PublicationKeys), nothing after its last member; it
// is submitted as one write once every member has passed (Writes.WriteCoordinator, "A write batch"). That and
// the conditions of the write path are everything the master checks again: the values of the root were
// validated by the child, which is the same engine with the schema the handshake agreed, as a child trusts a
// master's in a load (docs/decisions/replication-and-hosting.md, "Trust between the nodes of one database").
// A request that fails a check is a defect of the peer: nothing of it is executed, and the connection ends
// (ValidationFailed).
//
// The one answer without a check of the body: a request announced longer than the memory limit of the
// master is refused at its first frame with WriteRejected and Limit Memory, its other frames are dropped
// as they come, and the connection goes on. For every other request the bytes taken grow with the bytes
// received, never with the length announced.
//
// The answers. The write path records the outcome of every forwarded write as its reply before the write
// leaves the barrier of the close (Writes.WriteCoordinator, "Forwarded writes"): a success by the hold that
// publishes it, before its transaction (Writes.WriteExecutor), a refusal where its outcome is given
// (Writes.ForwardedOrigin.Refuse). The receiver awaits nothing of it. The sender of the connection sends
// them (Delivery.RecordedReplies). A request received whole is a submitted write: it runs although the
// connection ends, and its reply is dropped then; an incomplete request is dropped.
//
// One thread at a time uses a receiver: the task that receives on the connection. The submit holds the
// lock of the writes for as long as one write takes in memory, and waits for nothing else, except on one
// path: a forwarded write the arena refused twice with nothing admitted before it to wait for is resolved
// on the thread of its call, this task (Writes.WriteCoordinator, "The refusal", step 3), which waits there
// without a token for the reads that hold retired memory; a read leaves within one step (exception X6).
// Until then the Applied and the later requests of that child wait as well. So they wait while a batch is
// checked, every root of it on this task, and prepared and published in one hold of the lock of the writes.
internal sealed class RequestReceiver : IDisposable
{
    private readonly ForwardedWrites _writes;

    private readonly RecordedReplies _replies;

    private readonly MessageAssembler _requests;
    private readonly Action<RequestReceiver>? _observeFrame;

    private readonly BlockImport _import;

    // The correlation id of the last request of the connection.
    private ulong _lastId;

    public RequestReceiver(ForwardedWrites writes, RecordedReplies replies, Action<RequestReceiver>? observeFrame = null)
    {
        _writes = writes;
        _replies = replies;
        _observeFrame = observeFrame;
        _requests = new MessageAssembler(writes.Budget);
        _import = writes.NewImport();
    }

    // Takes one WriteRequest frame (see the class). ValidationFailed for a request that is not valid.
    public void Accept(in ReceivedFrame frame)
    {
        if (!_requests.InProgress)
        {
            if (frame.CorrelationId != _lastId + 1)
            {
                throw Protocol.Invalid($"A forwarded write has the correlation id {frame.CorrelationId}, where {_lastId + 1} comes next.");
            }

            _lastId = frame.CorrelationId;
        }

        var memoryLimit = _writes.MemoryLimit;

        var result = _requests.Accept(frame, memoryLimit, out var correlationId, out var body);
        _observeFrame?.Invoke(this);
        switch (result)
        {
            case MessageStepEnum.Refused:
                _replies.Refuse(correlationId, new LatticeException(
                    LatticeErrorEnum.WriteRejected,
                    $"The forwarded write announces more bytes than the memory budget of the master, {memoryLimit} bytes, can hold.")
                {
                    Limit = LatticeLimitEnum.Memory,
                },
                indexId: null, reserved: false);
                break;

            case MessageStepEnum.Whole:
                var lease = _requests.TakeLease();
                if (!_replies.TryReserve())
                {
                    lease?.Dispose();
                    _replies.Refuse(correlationId, new BudgetRefusedException(LatticeLimitEnum.Memory).WriteRejected(),
                        indexId: null, reserved: false);
                    break;
                }

                try
                {
                    Submit(correlationId, body, lease);
                }
                catch (BudgetRefusedException exception)
                {
                    lease?.Dispose();
                    _replies.Refuse(correlationId, exception.WriteRejected(), indexId: null);
                }
                catch
                {
                    _replies.ReleaseReservation();
                    lease?.Dispose();
                    throw;
                }
                break;
        }
    }

    public void Dispose()
    {
        _requests.Reset();
        _import.Dispose();
    }

    // Checks the request and submits it (see the class).
    private void Submit(ulong correlationId, ReadOnlySequence<byte> body, MemoryLease? lease)
    {
        var reader = new SequenceReader<byte>(body);

        if (ForwardEncoding.IsBatch(reader))
        {
            SubmitBatch(correlationId, ref reader, lease);

            return;
        }

        var request = ForwardEncoding.ReadRequestHead(ref reader);
        var table = TableOf(request.TypeId);
        var key = table.Keys.ReadKey(ref reader);
        var update = UpdateOf(table, request.Kind, ref reader, lease);
        var root = update is null ? RootOf(table, request, key, ref reader) : default;

        ForwardEncoding.ExpectEnd(reader);

        _writes.Coordinator.Forward(table, request.Kind, key, new ForwardedOrigin(_replies, correlationId, root, table.EntityType, lease),
            request.ExpectedRevision, SessionOf(request.MinimumPosition), update);
    }

    // Checks a write batch, every member and the batch as a whole, and submits it as one write (see the
    // class). The members' roots are slices of the body of the request.
    private void SubmitBatch(ulong correlationId, ref SequenceReader<byte> reader, MemoryLease? lease)
    {
        var (minimumPosition, count) = ForwardEncoding.ReadBatchHead(ref reader);
        var members = lease?.Allocate<BatchMember>(count, nameof(RequestReceiver)) ?? new BatchMember[count];
        var entityTypes = lease?.Allocate<LatticeEntityType>(count, nameof(RequestReceiver)) ?? new LatticeEntityType[count];
        var keys = PublicationKeys.Create(count, lease?.Budget);
        try
        {
            lease?.Own(keys, static (_, owned) => owned.Dispose());
        }
        catch
        {
            keys.Dispose();
            throw;
        }

        var commands = 0L;

        for (var place = 0; place < count; place++)
        {
            var member = ForwardEncoding.ReadMemberHead(ref reader);
            var table = TableOf(member.TypeId);
            var key = table.Keys.ReadKey(ref reader);
            var update = UpdateOf(table, member.Kind, ref reader, lease);
            var root = update is null ? RootOf(table, member, key, ref reader) : default;
            commands += update?.Commands.Length ?? 0;
            if (commands > int.MaxValue)
            {
                throw Protocol.Invalid("A forwarded write batch carries too many update commands.");
            }

            if (!keys.TryAdd(table, key))
            {
                throw Protocol.Invalid($"A forwarded write batch names {table.EntityType.Name} '{key}' in two members.");
            }

            members[place] = new BatchMember(member.Kind, table, key, member.ExpectedRevision, root, update);
            entityTypes[place] = table.EntityType;
        }

        ForwardEncoding.ExpectEnd(reader);

        _writes.Coordinator.Forward(members, keys, new ForwardedOrigin(_replies, correlationId, entityTypes, lease), SessionOf(minimumPosition));
    }

    private static UpdateInput? UpdateOf(EntityTable table, WriteKindEnum kind, ref SequenceReader<byte> reader, MemoryLease? lease)
    {
        if (kind != WriteKindEnum.Update)
        {
            return null;
        }

        var commands = new SequenceReader<byte>(ForwardEncoding.ReadRoot(ref reader));
        var decoded = UpdateCommands.ReadRequest(table.Layout, ref commands, lease);
        var outcome = lease?.Allocate(MemoryBudget.ObjectBytes(2L * IntPtr.Size), nameof(UpdateOutcome), static () => new UpdateOutcome()) ?? new();
        return UpdateInput.Create(decoded, outcome, lease);
    }

    // The table of an entity type the master stores.
    private EntityTable TableOf(uint typeId) =>
        _writes.Tables.TryGetStored(typeId, out var table)
            ? table
            : throw Protocol.Invalid($"A forwarded write names the entity type {typeId}, which the master does not store.");

    // The root of the write after its key, checked; empty for a delete, which has none.
    private ReadOnlySequence<byte> RootOf(EntityTable table, in ForwardedWrite write, in EntityKey key, ref SequenceReader<byte> reader) =>
        write.Kind == WriteKindEnum.Delete ? ReadOnlySequence<byte>.Empty : CheckRoot(table, write.TypeId, key, ForwardEncoding.ReadRoot(ref reader));

    // The session a forwarded write is ordered after, of the position the child gave; none when it gave none.
    private static LatticeSession? SessionOf(LatticePosition? minimumPosition) =>
        minimumPosition is { } position ? new LatticeSession(position) : null;

    // The root of an insert, a replace or an upsert, with its envelope: its type id is the one of the
    // request, its last mutation is 0, and the rest passes the check of a received root and holds the key
    // of the request. Returns the rest, which the write path places.
    private ReadOnlySequence<byte> CheckRoot(EntityTable table, uint typeId, in EntityKey key, ReadOnlySequence<byte> root)
    {
        var reader = new SequenceReader<byte>(root);

        if (!RootEnvelope.TryRead(ref reader, out var rootTypeId, out var lastMutation, out _))
        {
            throw Protocol.Invalid("The root of a forwarded write ends before its type id and its last mutation.");
        }

        if (rootTypeId != typeId || lastMutation != 0)
        {
            throw Protocol.Invalid("The root of a forwarded write names another entity type than the write, or a last mutation that is not 0.");
        }

        var rest = reader.UnreadSequence;
        var received = _import.Check(ref reader, table.Layout);

        if (!HoldsKey(table, received, key))
        {
            throw Protocol.Invalid($"The root of a forwarded write of {table.EntityType.Name} does not hold the key '{key}' of the write.");
        }

        return rest;
    }

    // Whether the checked root holds the key: the same integer or Guid, or the same text code unit for code
    // unit.
    private bool HoldsKey(EntityTable table, in ReceivedRoot received, in EntityKey key)
    {
        if (table.Keys.KeyKind == KeyKindEnum.Text)
        {
            return string.Equals(_import.TextKey(received), key.Text, StringComparison.Ordinal);
        }

        return table.Keys.KeysEqual(table.Keys.ReadFixedKey(_import.FixedKeyBytes(received)), key);
    }
}
