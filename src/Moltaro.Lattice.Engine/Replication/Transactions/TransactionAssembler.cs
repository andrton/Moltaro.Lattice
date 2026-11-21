using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Replication.Load;

namespace Moltaro.Lattice.Replication.Transactions;

// Puts the transactions a child receives together from their Transaction frames (TransactionFormat), one
// at a time, and hands out each whole: its sequence and its effects. Whether the sequence follows the one
// before, and what the effects hold, is the applier's (Engine.Child.Following).
//
// What it checks of a frame, before any of it is used:
// - While no transaction is in progress, the frame begins one: its sequence and the length of its
//   effects, at least one byte, then the first bytes of its effects, no more than the length announces.
//   The frame carries Protocol.EndsABody exactly when it holds all of them.
// - While one is in progress, the frame holds only its next bytes: no more than it still announces, and
//   the flag exactly when they complete it (AnnouncedBody).
// Each failure is a LatticeException with ValidationFailed. A transaction announced larger than the
// memory the child may take for its database cannot be held by it, whatever arrives after: that is a
// LatticeException with Unavailable and the reason, before anything is taken for it.
//
// A transaction of one frame is handed out where it lies, in the payload of its frame, so it is valid only
// until the next frame is received. One of several frames is collected in a TransferBuffer, which takes
// the pieces it needs as its bytes arrive and keeps them for the next large one: a child holds one
// transaction at a time outside its arena.
internal sealed class TransactionAssembler
{
    private readonly TransferBuffer _large = new();

    // The most bytes the effects of one transaction may announce: the memory the child may take for its
    // database.
    private readonly long _largest;

    // The transaction in progress: its sequence, and its effects, whose bytes are still to arrive.
    private ulong _sequence;

    private AnnouncedBody _effects;

    public TransactionAssembler(long largest)
    {
        _largest = largest;
    }

    // Whether a transaction is in progress: the next frame continues it, rather than beginning one.
    public bool InProgress => _effects.InProgress;

    // Reads one Transaction frame (see the class): true with the sequence and the effects of the
    // transaction it completes; false while the transaction needs more frames.
    public bool Accept(in ReceivedFrame frame, out ulong sequence, out ReadOnlySequence<byte> effects)
    {
        var payload = frame.Payload;

        if (_effects.InProgress)
        {
            return Continue(frame, out sequence, out effects);
        }

        if (payload.Length < TransactionFormat.HeaderSize)
        {
            throw Protocol.Invalid($"A Transaction frame of {payload.Length} bytes is shorter than the start of a transaction.");
        }

        sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);

        var announced = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span[sizeof(ulong)..]);
        var held = (ulong)(payload.Length - TransactionFormat.HeaderSize);

        if (announced == 0)
        {
            throw Protocol.Invalid($"The transaction {sequence} announces no effect.");
        }

        if (announced > (ulong)_largest)
        {
            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The master sends the transaction {sequence} of {announced} bytes, more than the memory limit of this child, {_largest} bytes, can hold.");
        }

        if (_effects.Begin(frame, announced, held, "transaction", sequence))
        {
            effects = new ReadOnlySequence<byte>(payload[TransactionFormat.HeaderSize..]);

            return true;
        }

        _large.Clear();
        _large.Write(payload.Span[TransactionFormat.HeaderSize..]);
        _sequence = sequence;
        effects = default;

        return false;
    }

    // Forgets the transaction in progress, when the connection it arrived on has ended: nothing of it is
    // applied.
    public void Reset()
    {
        _large.Clear();
        _effects.Reset();
    }

    // The next bytes of the transaction in progress.
    private bool Continue(in ReceivedFrame frame, out ulong sequence, out ReadOnlySequence<byte> effects)
    {
        var payload = frame.Payload.Span;

        sequence = _sequence;

        var complete = _effects.Continue(frame, payload.Length, "transaction", _sequence);

        _large.Write(payload);
        effects = complete ? _large.Sequence : default;

        return complete;
    }
}
