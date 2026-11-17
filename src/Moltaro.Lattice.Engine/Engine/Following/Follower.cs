using System.Buffers;
using System.Diagnostics;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Loading;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Replication.Transactions;

namespace Moltaro.Lattice.Engine.Following;

// How a child follows its master after its load (brief of Epic 1, sprint S11; the planner's decision of
// 17 November 2026 after the study, points 6 to 8, and the coordinator's reading with the tests): it
// receives the transactions the master sends after LoadEnd, puts each together whole
// (Replication.Transactions.TransactionAssembler), applies it under its own lock of the writes by the one
// rule of ReceivedChanges, and tells the master what it has applied.
//
// Before Ready (CatchUpAsync). The transactions of (S, R] are applied in their order, each received within
// OpenTimeout, as every part of a load is: the open of the child waits for them. Only then is the child
// Ready at R, its clock having moved from S through every sequence it applied. Nothing is acknowledged
// before R: the state before it is a mixture of the copy and the stream, which no position names.
//
// After Ready (FollowAsync). The child acknowledges R, then applies every transaction as it comes and,
// whenever no more of the stream has arrived whole, acknowledges the last one it applied
// (Applied(sequence), once for each burst it received). Reads go on meanwhile: they are protected by
// epochs, as on a master, and never wait for an application. Retired memory is freed by the applying
// thread (ReceivedChanges.Publish).
//
// What a transaction has to be. Its sequence follows the one before; it is checked by its frames and its
// announced length (TransactionAssembler); each effect is a Write, a Delete or an Unchanged of an entity
// type the engine stores, and the root of a Write carries the sequence of its transaction as its last
// mutation (the coordinator's decision of 17 November 2026), which, with consecutive sequences, keeps an
// effect not later than the state held from arriving after R. A transaction that is not so is refused
// whole: nothing of it is applied. Before Ready that fails the open, with ValidationFailed, which the open
// reports as Unavailable; after Ready the child stops following.
//
// Memory. A transaction the arena of the child cannot hold is applied by the rule of a write refused for
// memory: what is retired is freed and the transaction is tried again; refused again, the reads that hold
// retired memory are waited for, what they held is freed, and it is tried once more (the planner's block of
// 17 November 2026, point 5). Refused then, it cannot be held: before Ready the open fails with
// Unavailable; after it the child stops following. So does a transaction announced larger than the memory
// limit of the child, before anything is taken for it.
//
// Stopping. A child that stops following — a transaction it refused or cannot hold, a frame that is not
// valid, a connection that ended — ends its connection and keeps serving its reads at the position it has,
// Ready; it does not resume by itself (reconnection is S34). Nothing of a transaction it was putting
// together is applied.
//
// Suspension. A test holds back the application with Suspend (EngineDatabase.SuspendApplying): a
// transaction that is complete waits, before it is applied, until no suspension is left. The lock that
// guards the suspensions is taken alone, for a few instructions.
//
// The waits, and how each ends: the next frame, before Ready within OpenTimeout, after it until the master
// sends one or ends the connection — an idle master is not a lost one —, and the close of the child ends
// it; a suspension, until it is disposed or the child closes; a send of Applied, until the master has
// taken its bytes, which it does while it serves the child, and the close of the child ends it; the wait for
// the reads that hold retired memory, which leave within a step, and the close of the child ends it. Every
// application passes the barrier of the close, so that a close frees the arena only once no application is
// in progress; one after the close is refused, and the child stops.
internal sealed class Follower
{
    private readonly MasterLink _link;

    private readonly Lock _writes;

    private readonly WriteExecutor _executor;

    private readonly CloseBarrier _barrier;

    private readonly ReceivedChanges _changes;

    private readonly TransactionAssembler _assembler;

    private readonly TimeSpan _openTimeout;

    // The tables of the entity types of the schema, by the identity of their entity type.
    private readonly Dictionary<uint, EntityTable> _byTypeId;

    // Guards the count of the suspensions and the task their end completes.
    private readonly Lock _suspensions = new();

    private int _suspended;

    private TaskCompletionSource? _resumed;

    // The sequence of the last transaction applied, and of the last one acknowledged, once one was; the
    // task of the follower is the only one that changes them.
    private ulong _applied;

    private ulong _acknowledged;

    private bool _acknowledgedOnce;

    public Follower(MasterLink link, Lock writes, WriteExecutor executor, CloseBarrier barrier, ReceivedChanges changes, IEnumerable<EntityTable> tables, long memoryLimit, TimeSpan openTimeout)
    {
        _link = link;
        _writes = writes;
        _executor = executor;
        _barrier = barrier;
        _changes = changes;
        _assembler = new TransactionAssembler(memoryLimit);
        _openTimeout = openTimeout;
        _byTypeId = tables.ToDictionary(table => (uint)table.EntityType.Id);
    }

    // Applies the transactions after S up to R, which LoadEnd announced (see the class), and reports what
    // it applied. A failure passes through, and the open of the child fails.
    public async Task<CatchUpReport> CatchUpAsync(LoadEndBody end, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var applied = _changes.AppliedStates;
        var skipped = _changes.SkippedStates;

        _applied = end.Start;

        while (_applied < end.End)
        {
            await ApplyNextAsync(_openTimeout, cancellationToken).ConfigureAwait(false);
        }

        return new CatchUpReport(end.Start, end.End, end.Roots, Stopwatch.GetElapsedTime(started), _changes.AppliedStates - applied, _changes.SkippedStates - skipped);
    }

    // Follows the master once the child is Ready, until the child stops following or closes (see the
    // class). It never fails: what ends it is the end of the following, and the child stays Ready.
    public async Task FollowAsync(CancellationToken cancellationToken)
    {
        try
        {
            await AcknowledgeAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                await ApplyNextAsync(timeout: null, cancellationToken).ConfigureAwait(false);

                if (!_link.HoldsFrame)
                {
                    await AcknowledgeAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // The child stops following, whatever ended it (see the class): it keeps what it applied.
        }
        finally
        {
            _assembler.Reset();
            _link.Dispose();
        }
    }

    // Holds back the application of what the child receives until the suspension is disposed (see the
    // class).
    public IDisposable Suspend()
    {
        lock (_suspensions)
        {
            if (_suspended++ == 0)
            {
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new Suspension(this);
    }

    // The end of one suspension: the application goes on once none is left.
    public void Resume()
    {
        TaskCompletionSource? resumed = null;

        lock (_suspensions)
        {
            if (--_suspended == 0)
            {
                resumed = _resumed;
                _resumed = null;
            }
        }

        resumed?.TrySetResult();
    }

    private static LatticeException Invalid(string message) => new(LatticeErrorEnum.ValidationFailed, message);

    // Receives the next transaction whole and applies it.
    private async Task ApplyNextAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        ulong sequence;
        ReadOnlySequence<byte> effects;

        while (true)
        {
            var frame = await _link.ReceiveAsync(timeout, cancellationToken).ConfigureAwait(false);

            if (frame.Kind != FrameKindEnum.Transaction)
            {
                throw Invalid($"The master sent a {frame.Kind} frame where the stream of its transactions comes.");
            }

            if (_assembler.Accept(frame, out sequence, out effects))
            {
                break;
            }
        }

        if (sequence != _applied + 1)
        {
            throw Invalid($"The master sent the transaction {sequence} after {_applied}: a transaction is missing or repeated.");
        }

        // The effects stay where they were received: no frame is received while the suspension lasts.
        await WhileSuspendedAsync(cancellationToken).ConfigureAwait(false);
        Apply(sequence, effects, cancellationToken);

        _applied = sequence;
        _executor.WakePositions();
    }

    // Completes at once when no suspension is held, otherwise when the last one ends.
    private Task WhileSuspendedAsync(CancellationToken cancellationToken)
    {
        Task? resumed;

        lock (_suspensions)
        {
            resumed = _resumed?.Task;
        }

        return resumed is null ? Task.CompletedTask : resumed.WaitAsync(cancellationToken);
    }

    // Tells the master what the child has applied, unless it knows it already.
    private async ValueTask AcknowledgeAsync(CancellationToken cancellationToken)
    {
        if (_acknowledgedOnce && _applied == _acknowledged)
        {
            return;
        }

        await _link.SendAppliedAsync(_applied, cancellationToken).ConfigureAwait(false);
        _acknowledged = _applied;
        _acknowledgedOnce = true;
    }

    // Applies one transaction whole, by the rule of a write refused for memory (see the class).
    private void Apply(ulong sequence, ReadOnlySequence<byte> effects, CancellationToken cancellationToken)
    {
        _barrier.Enter();

        try
        {
            if (TryApply(sequence, effects))
            {
                return;
            }

            long epoch;
            bool retired;

            lock (_writes)
            {
                _executor.DrainRetired();
            }

            if (TryApply(sequence, effects))
            {
                return;
            }

            lock (_writes)
            {
                epoch = _executor.ReadsToWaitFor(out retired);
            }

            if (retired && epoch != 0)
            {
                _executor.WaitForReads(epoch, cancellationToken);

                lock (_writes)
                {
                    _executor.DrainRetired();
                }

                if (TryApply(sequence, effects))
                {
                    return;
                }
            }

            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The memory limit of this child cannot hold the transaction {sequence} of its master.");
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // Under the lock of the writes: prepares every effect of the transaction and publishes them, or none of
    // them. False when the arena refused one, with nothing of the transaction left.
    private bool TryApply(ulong sequence, ReadOnlySequence<byte> effects)
    {
        lock (_writes)
        {
            var reader = new SequenceReader<byte>(effects);

            try
            {
                while (!reader.End)
                {
                    if (!TryPrepareEffect(ref reader, sequence))
                    {
                        return false;
                    }
                }
            }
            catch
            {
                _changes.Abandon();
                throw;
            }

            _changes.Publish(sequence);

            return true;
        }
    }

    // Prepares the next effect of the transaction (see the class): false when the arena refused it.
    private bool TryPrepareEffect(ref SequenceReader<byte> reader, ulong sequence)
    {
        reader.TryRead(out var kind);

        switch ((EffectKindEnum)kind)
        {
            case EffectKindEnum.Write:
                if (!reader.TryReadLittleEndian(out long rootLength) || rootLength < RootAssembler.EnvelopeSize || rootLength > reader.Remaining)
                {
                    throw Invalid($"A Write of the transaction {sequence} announces a root its bytes do not hold.");
                }

                var root = new SequenceReader<byte>(reader.UnreadSequence.Slice(0, rootLength));

                reader.Advance(rootLength);
                root.TryReadLittleEndian(out int typeId);
                root.TryReadLittleEndian(out long lastMutation);

                var written = TableOf((uint)typeId, sequence);

                if ((ulong)lastMutation != sequence)
                {
                    throw Invalid($"A Write of the transaction {sequence} carries a root of {written.EntityType.Name} whose last mutation is {(ulong)lastMutation}.");
                }

                return _changes.TryPrepareWrite(written, sequence, ref root);

            case EffectKindEnum.Delete:
                if (!reader.TryReadLittleEndian(out int deletedId))
                {
                    throw Invalid($"A Delete of the transaction {sequence} ends before its type id.");
                }

                var deleted = TableOf((uint)deletedId, sequence);
                var key = deleted.ReadKey(ref reader);

                _changes.PrepareDelete(deleted, key, sequence);

                return true;

            case EffectKindEnum.Unchanged:
                return true;

            default:
                throw Invalid($"The transaction {sequence} holds an effect of the kind {kind}, which is none.");
        }
    }

    // The table of an entity type the engine stores.
    private EntityTable TableOf(uint typeId, ulong sequence) =>
        _byTypeId.TryGetValue(typeId, out var table) && table.Layout is not null
            ? table
            : throw Invalid($"The transaction {sequence} names the entity type {typeId}, which the schema of the child does not store.");
}
