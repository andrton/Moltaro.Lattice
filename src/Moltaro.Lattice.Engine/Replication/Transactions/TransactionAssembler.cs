using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Replication.Transactions;

// Puts the transactions a child receives together from their Transaction frames (TransactionFormat), one
// at a time, and hands out each whole: its sequence, the number of effects its header states and its
// effects. Whether the effects are valid, and as many as stated, is the applier's (Engine.Child.Following).
//
// What it checks of a frame, before any of it is used: while no transaction is in progress, the frame
// begins one, with the sequence that comes next, the length of its effects, at least one byte, and no more
// of them than the length announces; while one is in progress, the frame holds only its next bytes, no more
// than it still announces (AnnouncedBody). The frame carries Protocol.EndsABody exactly when it completes
// the transaction. Each failure is a LatticeException with ValidationFailed.
//
// Memory. A transaction of one frame is handed out where it lies, in the payload of its frame, so it is
// valid only until the next frame is received. One of several frames is collected in a TransferBuffer,
// charged to the budget of the child, which keeps its pieces for the next large one. The pieces a frame
// needs are taken before any of its bytes is accepted, and the framing state is changed only after them,
// so a frame the budget cannot hold, or a transaction announced larger than the limit of the child, is
// refused with BudgetRefusedException and changes nothing: the follower keeps the frame and tries it again
// (Engine.Child.Following.Follower).
//
// One thread at a time uses an assembler: the task that receives on the connection.
internal sealed class TransactionAssembler(long largest, MemoryBudget budget) : IDisposable
{
    private readonly TransferBuffer _large = new(budget);

    // The transaction in progress: its sequence, the number of its effects, and its effects, whose bytes are
    // still to arrive.
    private ulong _sequence;

    private uint _effectCount;

    private AnnouncedBody _effects;

    // Whether a transaction is in progress: the next frame continues it, rather than beginning one.
    public bool InProgress => _effects.InProgress;

    // Reads one Transaction frame (see the class): true with the sequence, the number of effects and the
    // effects of the transaction it completes; false while the transaction needs more frames. The tests of the engine
    // observe each attempt at a frame before anything of it is taken (beforeAttempt), with the frames of the
    // transaction received before it; expected is the sequence that comes next.
    public bool Accept(in ReceivedFrame frame, out ulong sequence, out uint effectCount, out ReadOnlySequence<byte> effects,
        Action<ulong, int>? beforeAttempt = null, int receivedFrames = 0, ulong? expected = null)
    {
        var payload = frame.Payload;
        var progress = _effects;
        if (progress.InProgress)
        {
            sequence = _sequence;
            effectCount = _effectCount;
            var finished = progress.Continue(frame, payload.Length, "transaction", sequence);
            beforeAttempt?.Invoke(sequence, receivedFrames);
            _large.PrepareAppend(payload.Length);
            _large.Write(payload.Span);
            _effects = progress;
            effects = finished ? _large.Sequence : default;
            return finished;
        }

        if (payload.Length < TransactionFormat.HeaderSize)
        {
            throw Protocol.Invalid($"A Transaction frame of {payload.Length} bytes is shorter than the start of a transaction.");
        }

        sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span);
        if (expected is { } next && sequence != next)
        {
            throw Protocol.Invalid($"The master sent the transaction {sequence} where {next} was expected: a transaction is missing or repeated.");
        }

        var announced = BinaryPrimitives.ReadUInt64LittleEndian(payload.Span[sizeof(ulong)..]);
        effectCount = TransactionFormat.ReadEffectCount(payload.Span);
        var bytes = payload[TransactionFormat.HeaderSize..];
        if (announced == 0)
        {
            throw Protocol.Invalid($"The transaction {sequence} announces no effect.");
        }

        var complete = progress.Begin(frame, announced, (ulong)bytes.Length, "transaction", sequence);
        beforeAttempt?.Invoke(sequence, receivedFrames);
        if (announced > (ulong)largest)
        {
            throw new BudgetRefusedException(LatticeLimitEnum.Memory);
        }

        if (complete)
        {
            effects = new ReadOnlySequence<byte>(bytes);
            return true;
        }

        _large.PrepareAppend(bytes.Length, fromStart: true);
        _large.Clear();
        _large.Write(bytes.Span);
        _sequence = sequence;
        _effectCount = effectCount;
        _effects = progress;
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

    // Adds the pieces of the buffer of transactions of several frames.
    public void ReportHeld(List<HeldArray> held) => _large.ReportHeld(held);

    // Releases the pieces, when the connection has ended.
    public void Dispose() => _large.Dispose();
}
