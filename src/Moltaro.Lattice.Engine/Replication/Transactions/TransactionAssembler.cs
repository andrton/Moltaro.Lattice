using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Replication.Transactions;

// The receiving task puts together one transaction at a time. It validates each frame against a
// copy of the framing state and reserves any additional pieces before accepting any of its bytes.
// A refused frame leaves the accepted prefix intact. A single-frame body borrows the frame buffer;
// a multi-frame body borrows the pieces, which remain charged and reusable between bodies.
internal sealed class TransactionAssembler(long largest, MemoryBudget? budget = null) : IDisposable
{
    private readonly TransferBuffer _large = new(budget);
    private ulong _sequence;
    private AnnouncedBody _effects;

    public bool InProgress => _effects.InProgress;

    public bool Accept(in ReceivedFrame frame, out ulong sequence, out ReadOnlySequence<byte> effects,
        Action<ulong, int>? beforeAttempt = null, int receivedFrames = 0, ulong? expected = null)
    {
        var payload = frame.Payload;
        var progress = _effects;
        if (progress.InProgress)
        {
            sequence = _sequence;
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
        var bytes = payload[TransactionFormat.HeaderSize..];
        if (announced == 0)
        {
            throw Protocol.Invalid($"The transaction {sequence} announces no effect.");
        }

        var complete = progress.Begin(frame, announced, (ulong)bytes.Length, "transaction", sequence);
        beforeAttempt?.Invoke(sequence, receivedFrames);
        if (announced > (ulong)largest)
        {
            if (budget is not null)
            {
                throw new BudgetRefusedException(LatticeLimitEnum.Memory);
            }

            throw new LatticeException(LatticeErrorEnum.Unavailable,
                $"The master sends the transaction {sequence} of {announced} bytes, more than the memory limit of this child, {largest} bytes, can hold.");
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
        _effects = progress;
        effects = default;
        return false;
    }

    public void Reset()
    {
        _large.Clear();
        _effects.Reset();
    }

    public void Dispose() => _large.Dispose();
}
