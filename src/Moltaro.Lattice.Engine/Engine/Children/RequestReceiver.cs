using System.Buffers;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Forwarding;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Children;

// What a master does with the writes one child forwards (brief of Epic 1, sprint S12; the planner's
// decisions after the study, points 1, 5, 12 and 13, and what he settled with the tests): it puts each
// request together from its frames, checks it, and submits it to its write path, in the order the requests
// arrive and without awaiting one before it submits the next (Writes.WriteCoordinator, "Forwarded
// writes"). Every frame of the child goes through the receiver of its connection (ChildConnection), and so
// do the requests, one at a time.
//
// What is checked, before anything of a request is used. The correlation id of each request is the one
// after the id of the request before, from 1: 0 stays the load and the stream, and an id given again would
// be one still in flight. The frames of a request (MessageAssembler). Its body (ForwardEncoding): a kind
// of write, no expected revision for an insert, an entity type the engine stores, a key of that type, and
// for an insert, a replace or an upsert a root of its entity type and a last mutation of 0, which passes
// the check a child makes of a root received in a load (Replication.Load.BlockImport.Check), and whose key
// is the key of the request, the text of a String key code unit for code unit. That and the conditions of
// the write path are everything the master checks again (point 1): the values of the root were validated
// by the child, which is the same engine with the schema the handshake agreed, as a child trusts a
// master's in a load. A request that fails a check is a defect of the peer: nothing of it is executed, and
// the connection ends (ValidationFailed).
//
// The one answer without a check of the body: a request announced longer than the memory limit of the
// master is refused at its first frame with WriteRejected and Limit Memory, its other frames are dropped
// as they come, and the connection goes on (point 13). For every other request the bytes taken grow with
// the bytes received, never with the length announced.
//
// The answers. The write path records the outcome of every forwarded write as its reply before the write
// leaves the barrier of the close (Writes.WriteCoordinator, "Forwarded writes"; decided at the review of
// S12, point 1): a success by the hold that publishes it, before its transaction (Writes.WriteExecutor), a
// refusal where its outcome is given (Writes.ForwardedOrigin.Refuse). The receiver awaits nothing of it.
// The sender of the connection sends them (Delivery.RecordedReplies). A request received whole is a
// submitted write: it runs although the connection ends, and its reply is dropped then (point 12); an
// incomplete request is dropped.
//
// One thread at a time uses a receiver: the task that receives on the connection. The submit holds the
// lock of the writes for as long as one write takes in memory, and waits for nothing else, except on one
// path: a forwarded write the arena refused twice with nothing admitted before it to wait for is resolved
// on the thread of its call, this task (Writes.WriteCoordinator, "The refusal", step 3), which waits there
// without a token for the reads that hold retired memory; a read leaves within one step (exception X6).
// Until then the Applied and the later requests of that child wait as well.
internal sealed class RequestReceiver
{
    private readonly ForwardedWrites _writes;

    private readonly RecordedReplies _replies;

    private readonly MessageAssembler _requests = new();

    private readonly BlockImport _import;

    // The correlation id of the last request of the connection.
    private ulong _lastId;

    public RequestReceiver(ForwardedWrites writes, RecordedReplies replies)
    {
        _writes = writes;
        _replies = replies;
        _import = writes.NewImport();
    }

    // Takes one WriteRequest frame (see the class). ValidationFailed for a request that is not valid.
    public void Accept(in ReceivedFrame frame)
    {
        if (!_requests.InProgress)
        {
            if (frame.CorrelationId != _lastId + 1)
            {
                throw Invalid($"A forwarded write has the correlation id {frame.CorrelationId}, where {_lastId + 1} comes next.");
            }

            _lastId = frame.CorrelationId;
        }

        var memoryLimit = _writes.MemoryLimit;

        switch (_requests.Accept(frame, memoryLimit, out var correlationId, out var body))
        {
            case MessageStepEnum.Refused:
                _replies.Refuse(correlationId, new LatticeException(
                    LatticeErrorEnum.WriteRejected,
                    $"The forwarded write announces more bytes than the memory budget of the master, {memoryLimit} bytes, can hold.")
                {
                    Limit = LatticeLimitEnum.Memory,
                });
                break;

            case MessageStepEnum.Whole:
                Submit(correlationId, body);
                break;
        }
    }

    private static LatticeException Invalid(string message) => new(LatticeErrorEnum.ValidationFailed, message);

    // Checks the request and submits it (see the class).
    private void Submit(ulong correlationId, ReadOnlySequence<byte> body)
    {
        var reader = new SequenceReader<byte>(body);
        var request = ForwardEncoding.ReadRequestHead(ref reader);

        if (!_writes.Tables.TryGetStored(request.TypeId, out var table))
        {
            throw Invalid($"A forwarded write names the entity type {request.TypeId}, which the master does not store.");
        }

        var key = table.ReadKey(ref reader);
        ReadOnlySequence<byte> root;

        if (request.Kind == WriteKindEnum.Delete)
        {
            ForwardEncoding.ExpectEnd(reader);
            root = ReadOnlySequence<byte>.Empty;
        }
        else
        {
            root = CheckRoot(table, request.TypeId, key, ForwardEncoding.ReadRoot(ref reader));
        }

        var session = request.MinimumPosition is { } position ? new LatticeSession(position) : null;

        _writes.Coordinator.Forward(table, request.Kind, key, new ForwardedOrigin(_replies, correlationId, root), request.ExpectedRevision, session);
    }

    // The root of an insert, a replace or an upsert, with its envelope: its type id is the one of the
    // request, its last mutation is 0, and the rest passes the check of a received root and holds the key
    // of the request. Returns the rest, which the write path places.
    private ReadOnlySequence<byte> CheckRoot(EntityTable table, uint typeId, in EntityKey key, ReadOnlySequence<byte> root)
    {
        var reader = new SequenceReader<byte>(root);

        if (!RootEnvelope.TryRead(ref reader, out var rootTypeId, out var lastMutation))
        {
            throw Invalid("The root of a forwarded write ends before its type id and its last mutation.");
        }

        if (rootTypeId != typeId || lastMutation != 0)
        {
            throw Invalid("The root of a forwarded write names another entity type than the write, or a last mutation that is not 0.");
        }

        var rest = reader.UnreadSequence;
        var received = _import.Check(ref reader, table.Layout!);

        if (!HoldsKey(table, received, key))
        {
            throw Invalid($"The root of a forwarded write of {table.EntityType.Name} does not hold the key '{key}' of the write.");
        }

        return rest;
    }

    // Whether the checked root holds the key: the same integer or Guid, or the same text code unit for code
    // unit.
    private bool HoldsKey(EntityTable table, in ReceivedRoot received, in EntityKey key)
    {
        if (table.KeyKind == KeyKindEnum.Text)
        {
            return string.Equals(_import.Text(received, table.EntityType.Key), key.Text, StringComparison.Ordinal);
        }

        return table.KeysEqual(table.StoredKey(_import.FixedPart(received)), key);
    }
}
