using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Replication.Forwarding;

// Puts forwarded writes or replies together from their frames, one message at a time: the frames of one
// message come one after another, the first beginning with the announced length of its body
// (OutgoingMessage), every frame of it with the same correlation id, the last one marked
// (Protocol.EndsABody) and no other.
//
// What it checks of a frame, before any of it is used:
// - While no message is in progress, the frame begins one: at least the announced length, then no more
//   bytes of the body than it announces, the flag exactly when they complete it.
// - While one is in progress, the frame carries its correlation id and only its next bytes: no more than
//   it still announces, and the flag exactly when they complete it (AnnouncedBody). A frame of another id
//   is a message interleaved with the one in progress, which the transport does not allow.
// Each failure is a LatticeException with ValidationFailed: the peer is not a node of this protocol.
//
// Memory. The body is copied out of the receive buffer frame by frame, a piece for each, so that the
// memory it takes grows with the bytes received and never with the length a frame announces. A body
// announced above the largest the caller takes is not taken at all: Accept says so at its first frame,
// and the frames after it are checked and dropped, so that the connection goes on. Every whole body is
// made of pieces of its own and stays valid after the next message begins: the caller hands it to a write
// that may wait (Engine.Children).
//
// One thread at a time uses an assembler: the task that receives on the connection.
internal sealed class MessageAssembler(MemoryBudget? budget = null, bool borrowsSingleFrame = false)
{
    // The message in progress: its correlation id, its body, whose bytes are still to arrive, whether it is
    // dropped, and the pieces taken so far; nothing is in progress while its body is not.
    private ulong _correlationId;

    private AnnouncedBody _message;

    private bool _dropping;

    private MessagePiece? _first;

    private MessagePiece? _last;

    private long _held;
    private MemoryLease? _lease;

    public MemoryLease? TakeLease() => Interlocked.Exchange(ref _lease, null);

    // Whether a message is in progress: the next frame continues it, rather than beginning one.
    public bool InProgress => _message.InProgress;

    // Reads one frame (see the class): with the correlation id of its message, and the body once it is
    // whole. A body announced above largest bytes is Refused at its first frame, and its last frame is
    // Dropped.
    public MessageStepEnum Accept(in ReceivedFrame frame, long largest, out ulong correlationId, out ReadOnlySequence<byte> body)
    {
        var payload = frame.Payload.Span;

        body = default;

        if (_message.InProgress)
        {
            correlationId = _correlationId;

            return Continue(frame, payload, out body);
        }

        correlationId = frame.CorrelationId;

        if (payload.Length < sizeof(ulong))
        {
            throw Protocol.Invalid($"The first {frame.Kind} frame of a message has {payload.Length} bytes, fewer than its announced length takes.");
        }

        var announced = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        var held = payload[sizeof(ulong)..];
        var complete = _message.Begin(frame, announced, (ulong)held.Length, "message", frame.CorrelationId);

        _correlationId = frame.CorrelationId;

        if (announced > (ulong)largest)
        {
            _dropping = !complete;

            return MessageStepEnum.Refused;
        }

        if (complete && borrowsSingleFrame)
        {
            body = new ReadOnlySequence<byte>(frame.Payload[sizeof(ulong)..]);
            return MessageStepEnum.Whole;
        }

        if (!TryTake(held, complete))
        {
            return MessageStepEnum.Refused;
        }

        if (complete)
        {
            body = TakeBody();

            return MessageStepEnum.Whole;
        }

        return MessageStepEnum.Partial;
    }

    // Forgets the message in progress, when the connection it arrived on has ended: nothing of it is used.
    public void Reset()
    {
        TakeLease()?.Dispose();
        _message.Reset();
        _dropping = false;
        _first = null;
        _last = null;
        _held = 0;
    }

    // The next bytes of the message in progress.
    private MessageStepEnum Continue(in ReceivedFrame frame, ReadOnlySpan<byte> payload, out ReadOnlySequence<byte> body)
    {
        body = default;

        if (frame.CorrelationId != _correlationId)
        {
            throw Protocol.Invalid($"A {frame.Kind} frame of the message {frame.CorrelationId} arrived in the middle of the message {_correlationId}.");
        }

        var complete = _message.Continue(frame, payload.Length, "message", _correlationId);

        if (_dropping)
        {
            if (!complete)
            {
                return MessageStepEnum.Partial;
            }

            _dropping = false;

            return MessageStepEnum.Dropped;
        }

        if (!TryTake(payload, complete))
        {
            return MessageStepEnum.Refused;
        }

        if (!complete)
        {
            return MessageStepEnum.Partial;
        }

        body = TakeBody();

        return MessageStepEnum.Whole;
    }

    private bool TryTake(ReadOnlySpan<byte> bytes, bool complete)
    {
        if (bytes.IsEmpty)
        {
            return true;
        }

        MessagePiece piece;
        try
        {
            if (budget is not null)
            {
                _lease ??= new MemoryLease(budget);
            }

            piece = MessagePiece.Create(bytes, _held, _lease);
        }
        catch (BudgetRefusedException)
        {
            TakeLease()?.Dispose();
            _first = null;
            _last = null;
            _held = 0;
            _dropping = !complete;
            return false;
        }

        _last?.Append(piece);
        _first ??= piece;
        _last = piece;
        _held += bytes.Length;
        return true;
    }

    // The body of the message that is whole, which is the caller's from now on.
    private ReadOnlySequence<byte> TakeBody()
    {
        var body = _first is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(_first, 0, _last!, _last!.Memory.Length);

        _first = null;
        _last = null;
        _held = 0;

        return body;
    }
}
