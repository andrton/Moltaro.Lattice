using System.Buffers;
using System.Buffers.Binary;

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
//   it still announces, and the flag exactly when they complete it. A frame of another id is a message
//   interleaved with the one in progress, which the transport does not allow.
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
internal sealed class MessageAssembler
{
    // The message in progress: its correlation id, the bytes still to arrive, whether it is dropped, and
    // the pieces taken so far; nothing is in progress while _remaining is 0.
    private ulong _correlationId;

    private ulong _remaining;

    private bool _dropping;

    private MessagePiece? _first;

    private MessagePiece? _last;

    private long _held;

    // Whether a message is in progress: the next frame continues it, rather than beginning one.
    public bool InProgress => _remaining != 0;

    // Reads one frame (see the class): with the correlation id of its message, and the body once it is
    // whole. A body announced above largest bytes is Refused at its first frame, and its last frame is
    // Dropped.
    public MessageStepEnum Accept(in ReceivedFrame frame, long largest, out ulong correlationId, out ReadOnlySequence<byte> body)
    {
        var payload = frame.Payload.Span;

        body = default;

        if (_remaining != 0)
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

        if ((ulong)held.Length > announced)
        {
            throw Protocol.Invalid($"The first {frame.Kind} frame of a message holds {held.Length} bytes of its body, more than the {announced} it announces.");
        }

        var complete = (ulong)held.Length == announced;

        if (complete != frame.EndsABody)
        {
            throw Protocol.Invalid(frame.EndsABody
                ? $"A {frame.Kind} message announces {announced} bytes, and its last frame ends after {held.Length}."
                : $"A {frame.Kind} message is complete in its first frame, which is not marked as its end.");
        }

        if (announced > (ulong)largest)
        {
            if (!complete)
            {
                _correlationId = frame.CorrelationId;
                _remaining = announced - (ulong)held.Length;
                _dropping = true;
            }

            return MessageStepEnum.Refused;
        }

        Take(held);

        if (complete)
        {
            body = TakeBody();

            return MessageStepEnum.Whole;
        }

        _correlationId = frame.CorrelationId;
        _remaining = announced - (ulong)held.Length;

        return MessageStepEnum.Partial;
    }

    // Forgets the message in progress, when the connection it arrived on has ended: nothing of it is used.
    public void Reset()
    {
        _remaining = 0;
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

        if ((ulong)payload.Length > _remaining)
        {
            throw Protocol.Invalid($"A {frame.Kind} frame holds {payload.Length} bytes of the message {_correlationId}, which announces {_remaining} more.");
        }

        _remaining -= (ulong)payload.Length;

        if ((_remaining == 0) != frame.EndsABody)
        {
            throw Protocol.Invalid(frame.EndsABody
                ? $"A {frame.Kind} frame is marked as the end of the message {_correlationId}, which announces {_remaining} more bytes."
                : $"A {frame.Kind} frame completes the message {_correlationId} and is not marked as its end.");
        }

        if (_dropping)
        {
            if (_remaining != 0)
            {
                return MessageStepEnum.Partial;
            }

            _dropping = false;

            return MessageStepEnum.Dropped;
        }

        Take(payload);

        if (_remaining != 0)
        {
            return MessageStepEnum.Partial;
        }

        body = TakeBody();

        return MessageStepEnum.Whole;
    }

    private void Take(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var piece = new MessagePiece(bytes, _held);

        _last?.Append(piece);
        _first ??= piece;
        _last = piece;
        _held += bytes.Length;
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
