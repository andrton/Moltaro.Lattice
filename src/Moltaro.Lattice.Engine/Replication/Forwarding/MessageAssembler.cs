using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using Moltaro.Lattice.Storage.Budgeting;

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
// The bytes of every piece are charged to the budget given before the piece is made, as buffers of the
// connection (docs/design/storage-and-maintenance.md, the memory budget); a piece the budget cannot hold
// refuses the message, which is then dropped as one announced too large. The charge of a whole body passes
// to the caller with TakeCharge, which the caller makes before the next frame, and releases when it is
// done with the body.
//
// One thread at a time uses an assembler: the task that receives on the connection.
internal sealed class MessageAssembler(MemoryBudget budget)
{
    // The message in progress: its correlation id, its body, whose bytes are still to arrive, whether it is
    // dropped, and the pieces taken so far; nothing is in progress while its body is not.
    private ulong _correlationId;

    private AnnouncedBody _message;

    private bool _dropping;

    private MessagePiece? _first;

    private MessagePiece? _last;

    private long _held;

    // The bytes charged for the pieces of the message in progress, or of the whole body until the caller
    // takes their charge.
    private long _charged;

    // Whether a message is in progress: the next frame continues it, rather than beginning one.
    public bool InProgress => _message.InProgress;

    // The charge of the whole body Accept returned last, which is the caller's from now on; the pieces
    // are no longer the assembler's either.
    public long TakeCharge()
    {
        var charged = _charged;
        _charged = 0;
        _first = null;
        _last = null;
        _held = 0;
        return charged;
    }

    // Adds the bytes of each piece of the message in progress, or of the whole body not taken yet.
    public void ReportHeld(List<HeldArray> held)
    {
        for (var piece = _first; piece is not null; piece = (MessagePiece?)piece.Next)
        {
            held.Add(new HeldArray(nameof(MessageAssembler), typeof(byte), piece.Memory.Length));
        }
    }

    // Reads one frame (see the class): with the correlation id of its message, and the body once it is
    // whole. A body announced above largest bytes is Refused at its first frame, and one whose piece the
    // budget cannot hold at that frame; the last frame of a refused body that did not end there is
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
        Debug.Assert(_charged == 0, "The charge of the last whole body was taken before the next message.");

        if (payload.Length < sizeof(ulong))
        {
            throw Protocol.Invalid($"The first {frame.Kind} frame of a message has {payload.Length} bytes, fewer than its announced length takes.");
        }

        var announced = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        var held = payload[sizeof(ulong)..];
        var complete = _message.Begin(frame, announced, (ulong)held.Length, "message", frame.CorrelationId);

        _correlationId = frame.CorrelationId;

        if (announced > (ulong)largest || !TryTake(held))
        {
            _dropping = !complete;

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
        _message.Reset();
        _dropping = false;
        budget.Release(TakeCharge());
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

        // A piece the budget cannot hold refuses the message as its first frame would; its later frames
        // are dropped.
        if (!TryTake(payload))
        {
            _dropping = !complete;

            return MessageStepEnum.Refused;
        }

        if (!complete)
        {
            return MessageStepEnum.Partial;
        }

        body = TakeBody();

        return MessageStepEnum.Whole;
    }

    // Copies the bytes into a piece charged before it is made. False when the budget cannot hold it:
    // the pieces of the message are released, and the rest of it is dropped.
    private bool TryTake(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return true;
        }

        var bytesOfPiece = MemoryBudget.ArrayBytes<byte>(bytes.Length);
        if (!budget.TryReserve(bytesOfPiece))
        {
            budget.Release(TakeCharge());
            return false;
        }

        MessagePiece piece;
        try
        {
            piece = new MessagePiece(bytes, _held);
        }
        catch
        {
            budget.Release(bytesOfPiece);
            throw;
        }

        _charged += bytesOfPiece;
        _last?.Append(piece);
        _first ??= piece;
        _last = piece;
        _held += bytes.Length;
        return true;
    }

    // The body of the message that is whole. Its pieces and their charge stay with the assembler until the
    // caller takes them (TakeCharge).
    private ReadOnlySequence<byte> TakeBody() =>
        _first is null ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(_first, 0, _last!, _last!.Memory.Length);
}
