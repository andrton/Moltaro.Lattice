using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// The write coordinator of a master (brief of Epic 1, sprint S09; milestone contract, "Write
// coordinator and revision semantics"). Many callers write at once; the coordinator orders, bounds and
// publishes their writes. A write passes three steps:
// - Admission, short, under the lock of the coordinator, on the thread of the call before the call
//   returns its task: the write reserves its identity, entity type and key as the schema compares keys
//   (WriteIdentity), in a first-in-first-out chain of that identity. A write whose identity an earlier
//   one holds waits its turn there and holds nothing in the engine. So writes to one entity run in the
//   order their calls were made: a call made after an earlier call returned its task is admitted after
//   it, and no write of the entity is prepared before the one ahead of it is published or withdrawn.
// - Preparation, of at most PrepareWorkers writes at once, each holding its identity
//   (WriteExecutor.TryPrepare): it looks its key up inside the protection of reads, checks its
//   condition, and encodes its values into prepared blocks, taking the lock of the writes for each
//   change of a structure of the engine alone. A write that holds its identity and finds no place
//   among the writes being prepared waits for one, and holds nothing in the engine meanwhile.
// - Commit, in one order: the commit stage takes the writes prepared when it is free into a group, at
//   most CommitGroup.MaxOperations writes or CommitGroup.MaxEncodedSize bytes, a larger write alone,
//   and never waits for a group to fill; it hands the group to the storage step (ICommitStorage), whose
//   call is the dispatch, and publishes it under the lock of the writes (WriteExecutor.Publish). The
//   writes of a group take consecutive positions from CommitOutcome.FirstSequence in its order.
//
// The threads. A write is prepared on the thread of its call when a place among the writes being
// prepared is free at its admission, and the commit stage runs on that thread too when it is free, so
// a caller alone pays no hand-over between threads: its write is admitted, prepared, committed and
// published within the call, and the task it returns is complete. A write that has to wait is taken
// further by the thread that ends what it waits for, which hands it to the thread pool
// (ThreadPool.UnsafeQueueUserWorkItem, with the write as the work item, so that nothing more is
// allocated): the end of the earlier write of its identity, the end of a preparation, which passes its
// place to the next write waiting for one, and the end of the writes admitted before a write the arena
// refused. A thread of the pool that prepared a write goes on with the next one waiting while it does
// not become the commit stage. The commit stage, once taken, publishes group after group while
// prepared writes are there; on the thread of a call it stops once the write of that call is
// published and gives the stage up, with a work item of the thread pool that takes it when nobody else
// has, so that no call waits for the writes of others and no prepared write is left without a stage.
// A call whose write it prepared finds the stage busy spins a little, outside every lock, until its
// write has its outcome or the stage is free to take (AwaitCommitStage), as a waiter of the combining
// technique of Hendler, Incze, Shavit and Tzafrir ("Flat combining and the synchronization-parallelism
// tradeoff", 2010; the idea, no code) waits for its request to be served or the combiner to be free:
// the call then returns a complete task, and its caller is not woken by another thread. Measured on
// the development machine, three runs each against the same design without it: 4 callers that insert
// and delete made 462,000 to 713,000 writes a second, against 355,000 to 423,000, and 16 callers that
// replace 626,000 to 661,000, against 554,000 to 603,000; one caller and the readers beside the callers
// were as fast with it as without. Where the storage step does not complete at once, the stage goes on
// where it completes, and the call returns. The task of a write completes on the thread pool
// (PendingWrite), never inside a lock and never by running code of the application on a thread of the
// engine.
//
// The locks, in their order. The coordinator has the lock of the coordinator, under which it changes
// the chains of the identities, the list of the writes admitted, its two queues, the places of the
// writes being prepared, whether the commit stage is taken, its counters and whether the database
// faulted. The executor has the lock of the writes (WriteExecutor). The two are never held together:
// the coordinator calls the executor, and the storage step, only after it has released its own lock,
// and the executor takes no other lock. No lock is held across an await, a wait or a call of the
// storage step, and none is taken inside the protection of reads. A thread that holds the lock of the
// coordinator takes no other lock.
//
// Why the hand-overs are ordered (the .NET memory model, not that of x64). A write passes from thread
// to thread only through the lock of the coordinator and the work items of the thread pool, which
// release what a thread wrote before them and acquire it on the thread that goes on, so every step of
// a write sees what the steps before it wrote: the thread that prepares it writes its blocks, its root
// entry and its PreparedWrite, then takes the lock to put it into the queue of the commit stage; the
// stage takes the lock to take it into a group, then the lock of the writes to publish it, and the
// stores that publish it have release semantics (WriteExecutor), so a read that sees them sees the
// blocks. The next write of an identity is started under the lock after the one before it was
// published, so it looks the entity up as that write left it. The fields of a write that the lock does
// not guard are written by one thread before such a hand-over and read after it (PendingWrite).
//
// What a write holds, and until when. From its admission to its outcome it holds an entry of the
// barrier of the close (CloseBarrier), so a close lets every write admitted before it reach its
// outcome and frees the arena only after the last has ended; and its identity, so that no other write
// of its entity overtakes it. From its preparation to its publication or withdrawal it holds what the
// preparation made: prepared blocks, which ArenaCounters.PreparedBytes counts, for an insert a root
// entry and room in the index, for a replace or a delete a place in the retired list.
//
// Cancellation (decision "Cancellation at the commit boundary"; brief of S09, "Dispatch"). Until its
// dispatch a write whose token is cancelled is withdrawn: it releases what it prepared, leaves the
// lists of the coordinator, and its call ends in OperationCanceledException, after nothing of it is
// left in QueuedWrites or in the counters of the engine. A write that waits is withdrawn by the thread
// that cancels its token (Cancel); one being prepared looks at its token when its preparation ends,
// and while it waits for reads. From the dispatch on the token is not looked at, and the call returns
// the real outcome.
//
// The refusal (brief of S09, "The refusal"; decision Q14 as amended on 16 November 2026). A write the
// arena refuses has freed everything it took. It frees what is retired and tries again. While it is
// still refused it waits for every write admitted before it to complete, holding its identity and
// nothing in the engine and giving up its place among the writes being prepared, so that the writes it
// waits for can be prepared; then it waits, outside every lock, for the reads that hold retired
// memory, frees it and tries again. It is refused, with WriteRejected and Limit Memory, only when every
// write admitted before it has completed, nothing retired is left and the arena still has no room. A
// write refused so has had no effect and never runs later. The waits are finite (exception X6): the
// writes admitted before it are finite and each of them ends, and none of them waits for it, since it
// waits only for writes admitted before it and holds nothing any of them needs; a read leaves within a
// step. Its token cancels both waits. The switch of the tests runs when it begins either
// (HoldNextWait).
//
// A fault (brief of S09, "Faulted" and the answers of the planner, "After a fault"). A failure of the
// storage step or of a publication faults the database: reads are refused from then on, a read that
// retries on the mark of the publication that failed gives up, and the waits for a position end
// (EngineDatabase.Fault). The writes of the group published before the failure get their results; the
// write whose publication failed and the writes of the group after it get IndeterminateOutcome, the
// first with the failure as its InnerException; every other write admitted gets Unavailable once it
// has released what it held: at once when it waits, at the end of its preparation when it is being
// prepared. Every later write is Unavailable, and a close works.
internal sealed class WriteCoordinator : IThreadPoolWorkItem
{
    // How many writes are prepared at once: the starting value of the milestone contract, internal and
    // no option, since it refuses nothing (brief of S09, "PrepareWorkers is no public option").
    private const int PrepareWorkers = 2;

    // The lock of the coordinator (see the class).
    private readonly Lock _lock = new();

    private readonly WriteExecutor _executor;

    private readonly ICommitStorage _storage;

    private readonly CloseBarrier _barrier;

    private readonly PositionClock _clock;

    // How long a write waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    // Faults the database (EngineDatabase): its state, its reads and its waits for a position.
    private readonly Action _faultDatabase;

    // For every identity a write holds, the last write of it admitted: the end of its chain.
    private readonly Dictionary<WriteIdentity, PendingWrite> _identities = [];

    // The group in the commit stage, formed anew for every commit.
    private readonly CommitGroup _group = new();

    // The writes admitted and not completed, in the order of their admission.
    private PendingWrite? _oldest;

    private PendingWrite? _newest;

    // The writes that hold their identity and wait for a place among the writes being prepared, first
    // in first out, but for a write the arena refused, which comes first once the writes before it
    // completed. A write withdrawn while it waits here stays in the queue, completed, and is passed over.
    private PendingWrite? _readyFirst;

    private PendingWrite? _readyLast;

    // The writes prepared and waiting for the commit stage, in the order their preparation ended. A
    // write withdrawn here stays in the queue and is passed over.
    private PendingWrite? _preparedFirst;

    private PendingWrite? _preparedLast;

    // The writes being prepared, at most PrepareWorkers.
    private int _preparing;

    // Whether a thread runs the commit stage. Read without the lock by a caller that waits for it.
    private volatile bool _committing;

    // Set once the storage step or a publication failed; never cleared.
    private volatile bool _faulted;

    // The counters (EngineCounters), written under the lock and read by the counters of the database
    // without it.
    private int _queued;

    private int _largestGroup;

    private long _largestGroupBytes;

    // What the next write the arena refused runs when it begins to wait, for the tests of the engine
    // (EngineDatabase.HoldNextWaitForReads); null when no test armed it. Only a refused write loads it.
    private Action? _waitHold;

    public WriteCoordinator(WriteExecutor executor, ICommitStorage storage, CloseBarrier barrier, PositionClock clock, TimeSpan synchronizationTimeout, Action faultDatabase)
    {
        _executor = executor;
        _storage = storage;
        _barrier = barrier;
        _clock = clock;
        _synchronizationTimeout = synchronizationTimeout;
        _faultDatabase = faultDatabase;
    }

    // The writes admitted and not completed (EngineCounters.QueuedWrites).
    public int QueuedWrites => Volatile.Read(ref _queued);

    // The most writes, and the most bytes, one group held since the open, each taken by itself.
    public int LargestCommitGroup => Volatile.Read(ref _largestGroup);

    public long LargestCommitGroupBytes => Volatile.Read(ref _largestGroupBytes);

    // Submits a write and returns its task, with its position and whether it changed the database. The
    // values are those of a complete entity for an insert, a replace and an upsert, and null for a
    // delete. The token cancels the write while it waits for its session's position and until its
    // dispatch (see the class). Refusals, in the task: NotSupportedException for an entity type the
    // engine does not store; for the session, HistoryMismatch, SynchronizationTimeout and Unavailable;
    // then ObjectDisposedException after the close and Unavailable after a fault; RevisionConflict,
    // DuplicateKey and NotFound for its condition; WriteRejected, with Limit Memory, for an entity the
    // memory budget cannot hold; IndeterminateOutcome when a publication failed after the storage step
    // took it. ValidationFailed for text that is not well-formed or a value above the bound of one value
    // comes from the guard of the root writer, only for values that the validation of the core did not
    // check.
    public ValueTask<LatticeWriteResult> SubmitAsync(
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
            return ValueTask.FromException<LatticeWriteResult>(new NotSupportedException(table.Refusal));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<LatticeWriteResult>(cancellationToken);
        }

        if (session is not null)
        {
            ValueTask waited;

            try
            {
                waited = _clock.WaitAsync(session, _synchronizationTimeout, cancellationToken);
            }
            catch (Exception exception)
            {
                return ValueTask.FromException<LatticeWriteResult>(exception);
            }

            // On a master the position of a session is published already, unless a caller made it up.
            if (!waited.IsCompletedSuccessfully)
            {
                return AdmitAfterAsync(waited, table, kind, key, values, expectedRevision, session, cancellationToken);
            }
        }

        return Admit(table, kind, key, values, expectedRevision, session, cancellationToken);
    }

    // The cancellation of the token of a write, on the thread that cancels it: withdraws a write that
    // waits, and one that is prepared and waits for the commit stage; nothing for one being prepared,
    // which looks at its token itself, nor for one dispatched.
    public void Cancel(PendingWrite write)
    {
        PendingWrite? started = null;
        var end = false;
        var withdraw = false;

        lock (_lock)
        {
            switch (write.State)
            {
                case PendingWriteStateEnum.WaitingForIdentity or PendingWriteStateEnum.WaitingToPrepare or PendingWriteStateEnum.WaitingForEarlier:
                    started = Finish(write);
                    end = true;
                    break;

                case PendingWriteStateEnum.Prepared:
                    write.State = PendingWriteStateEnum.Withdrawing;
                    withdraw = true;
                    break;
            }
        }

        if (withdraw)
        {
            _executor.Release(write.Prepared!);

            lock (_lock)
            {
                started = Finish(write);
            }

            end = true;
        }

        if (end)
        {
            End(write, new OperationCanceledException(write.Token));
            Start(started);
        }
    }

    // Prepares a write that waited for its turn, on a thread of the pool (PendingWrite.Execute).
    public void Prepare(PendingWrite write) => RunPreparation(write, inline: false);

    // Arms the switch of the tests: the next write the arena refused runs hold once, when it begins to
    // wait (EngineDatabase.HoldNextWaitForReads).
    public void HoldNextWait(Action hold) => Volatile.Write(ref _waitHold, hold);

    // Arms a fault point for the tests of the engine (EngineDatabase.ArmFault).
    public void ArmFault(EngineFaultPointEnum point, int skippedPasses) => _executor.Arm(point, skippedPasses);

    // The work item that a call whose write is published leaves when it gives the stage up with prepared
    // writes left (see the class): takes the stage unless a caller that waited for it, or a write whose
    // preparation ended, took it first.
    void IThreadPoolWorkItem.Execute()
    {
        bool commit;

        lock (_lock)
        {
            commit = !_committing && !_faulted && FormGroup();
            _committing |= commit;
        }

        if (commit)
        {
            _ = CommitAsync(own: null);
        }
    }

    private static LatticeException Unavailable() =>
        new(LatticeErrorEnum.Unavailable, "The database is Faulted: a publication failed, and it serves nothing until a new instance is opened.");

    // A write with a session whose position is not published yet: admitted once it is.
    private async ValueTask<LatticeWriteResult> AdmitAfterAsync(
        ValueTask waited,
        EntityTable table,
        WriteKindEnum kind,
        EntityKey key,
        object?[]? values,
        LatticeRevision? expectedRevision,
        LatticeSession session,
        CancellationToken cancellationToken)
    {
        await waited.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return await Admit(table, kind, key, values, expectedRevision, session, cancellationToken).ConfigureAwait(false);
    }

    // Admission (see the class), then the preparation on this thread when the write may be prepared at
    // once.
    private ValueTask<LatticeWriteResult> Admit(
        EntityTable table,
        WriteKindEnum kind,
        in EntityKey key,
        object?[]? values,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken cancellationToken)
    {
        try
        {
            _barrier.Enter();
        }
        catch (ObjectDisposedException exception)
        {
            return ValueTask.FromException<LatticeWriteResult>(exception);
        }

        var write = new PendingWrite(this, table, kind, key, values, expectedRevision, session, cancellationToken);
        var prepareHere = false;
        bool faulted;

        lock (_lock)
        {
            faulted = _faulted;

            if (!faulted)
            {
                // Last in the list of the writes admitted.
                write.Older = _newest;

                if (_newest is null)
                {
                    _oldest = write;
                }
                else
                {
                    _newest.Newer = write;
                }

                _newest = write;
                _queued++;

                ref var last = ref CollectionsMarshal.GetValueRefOrAddDefault(_identities, new WriteIdentity(table, key), out var reserved);

                if (reserved)
                {
                    // Behind the last write of its identity.
                    last!.LaterOfIdentity = write;
                    write.EarlierOfIdentity = last;
                    write.State = PendingWriteStateEnum.WaitingForIdentity;
                }
                else
                {
                    prepareHere = TakePlaceOrWait(write, first: false);
                }

                last = write;
            }
        }

        if (faulted)
        {
            _barrier.Exit();

            return ValueTask.FromException<LatticeWriteResult>(Unavailable());
        }

        if (cancellationToken.CanBeCanceled)
        {
            Register(write);
        }

        if (prepareHere)
        {
            RunPreparation(write, inline: true);
        }

        return write.Task;
    }

    // Registers the cancellation of the token of the write. A token already cancelled runs Cancel
    // here, at once. The registration is kept under the lock, so that the end of the write, which
    // unregisters it, finds it, or the write has ended before and it is unregistered here.
    private void Register(PendingWrite write)
    {
        var registration = write.Token.UnsafeRegister(static state => Cancelled((PendingWrite)state!), write);
        bool ended;

        lock (_lock)
        {
            ended = write.State == PendingWriteStateEnum.Completed;

            if (!ended)
            {
                write.Registration = registration;
            }
        }

        if (ended)
        {
            registration.Unregister();
        }
    }

    private static void Cancelled(PendingWrite write) => write.Coordinator.Cancel(write);

    // Under the lock: a place among the writes being prepared for the write, which holds its identity,
    // or a place in the queue of those that wait for one; first puts it at the front. True when it has
    // a place to be prepared.
    private bool TakePlaceOrWait(PendingWrite write, bool first)
    {
        if (_preparing < PrepareWorkers)
        {
            _preparing++;
            write.State = PendingWriteStateEnum.Preparing;

            return true;
        }

        write.State = PendingWriteStateEnum.WaitingToPrepare;

        if (_readyFirst is null)
        {
            _readyFirst = write;
            _readyLast = write;
        }
        else if (first)
        {
            write.NextInQueue = _readyFirst;
            _readyFirst = write;
        }
        else
        {
            _readyLast!.NextInQueue = write;
            _readyLast = write;
        }

        return false;
    }

    // Under the lock, at the end of a preparation: its place passes to the next write that waits for
    // one, which is returned to be prepared, or is given back.
    private PendingWrite? PassPlace()
    {
        while (_readyFirst is { } ready)
        {
            _readyFirst = ready.NextInQueue;
            ready.NextInQueue = null;

            if (_readyFirst is null)
            {
                _readyLast = null;
            }

            if (ready.State == PendingWriteStateEnum.WaitingToPrepare)
            {
                ready.State = PendingWriteStateEnum.Preparing;

                return ready;
            }
        }

        _preparing--;

        return null;
    }

    // Prepares the write, which has a place, and goes on with what the end of its preparation gives
    // this thread (see the class): the commit stage, or, on a thread of the pool, the next write that
    // took the place.
    private void RunPreparation(PendingWrite write, bool inline)
    {
        var current = write;

        while (current is not null)
        {
            var next = PrepareOne(current, out var commit, out var waitsForStage);

            if (commit)
            {
                Schedule(next);
                _ = CommitAsync(inline ? write : null);

                return;
            }

            if (inline)
            {
                Schedule(next);

                if (waitsForStage)
                {
                    AwaitCommitStage(write);
                }

                return;
            }

            current = next;
        }
    }

    // A caller whose write is prepared and waits for the commit stage: spins a little, outside every
    // lock, until its write has its outcome or the stage is free, and then takes the stage itself, so
    // that the call returns its outcome instead of waiting for another thread to complete it (see the
    // class). The spin is bounded: SpinWait, until its next spin would give up the processor, some
    // microseconds; then the call returns the task of the write, which the stage completes.
    private void AwaitCommitStage(PendingWrite write)
    {
        var spinner = default(SpinWait);

        while (!spinner.NextSpinWillYield)
        {
            spinner.SpinOnce(sleep1Threshold: -1);

            if (_committing)
            {
                continue;
            }

            bool commit;

            lock (_lock)
            {
                if (write.State != PendingWriteStateEnum.Prepared)
                {
                    if (write.State == PendingWriteStateEnum.Completed)
                    {
                        return;
                    }

                    continue;
                }

                commit = !_committing && !_faulted && FormGroup();
                _committing |= commit;
            }

            if (commit)
            {
                _ = CommitAsync(write);

                return;
            }
        }
    }

    // One preparation and its end, under the lock: the place passes on, and the write goes to the
    // commit stage, taking it when it is free (commit) or waiting for it (waitsForStage), or is
    // withdrawn, or ends, or waits for the writes admitted before it. Returns the write the place
    // passed to, if any.
    private PendingWrite? PrepareOne(PendingWrite write, out bool commit, out bool waitsForStage)
    {
        var prepared = default(PreparedWrite);
        var failure = default(Exception);
        var parked = false;

        try
        {
            parked = !TryPrepare(write, out prepared);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        PendingWrite? next;
        PendingWrite? started = null;
        var withdraw = false;

        commit = false;
        waitsForStage = false;

        lock (_lock)
        {
            next = PassPlace();

            if (failure is not null)
            {
                // After a fault a write being prepared did not run: Unavailable, whatever it met.
                if (_faulted)
                {
                    failure = Unavailable();
                }

                started = Finish(write);
            }
            else if (!parked)
            {
                if (_faulted || write.Token.IsCancellationRequested)
                {
                    write.State = PendingWriteStateEnum.Withdrawing;
                    failure = _faulted ? Unavailable() : new OperationCanceledException(write.Token);
                    withdraw = true;
                }
                else
                {
                    write.Prepared = prepared;
                    write.State = PendingWriteStateEnum.Prepared;
                    EnqueuePrepared(write);

                    if (!_committing)
                    {
                        _committing = true;
                        commit = FormGroup();
                        _committing = commit;
                    }

                    waitsForStage = !commit;
                }
            }

            // A write that waits for the writes admitted before it is another thread's from the moment
            // it waits (WaitForEarlier): nothing of it is touched here.
        }

        if (withdraw)
        {
            _executor.Release(prepared!);

            lock (_lock)
            {
                started = Finish(write);
            }
        }

        if (failure is not null)
        {
            End(write, failure);
            Start(started);
        }

        return next;
    }

    // The preparation of a write with the refusal of the arena (see the class): true when it is
    // prepared, false when it waits for the writes admitted before it. A failure, the final refusal
    // among them, passes through, with nothing of the write left in the engine.
    private bool TryPrepare(PendingWrite write, out PreparedWrite? prepared)
    {
        if (_faulted)
        {
            throw Unavailable();
        }

        // A write the arena refused, whose earlier writes have completed since: what they retired may
        // be held by reads.
        if (write.Refused)
        {
            WaitForReadsAndDrain(write);
        }

        while (true)
        {
            if (_executor.TryPrepare(write, out prepared, out var refusal))
            {
                return true;
            }

            write.Refusal = refusal;

            // The first refusal: what is retired is freed, and the write tries again.
            if (!write.Refused)
            {
                write.Refused = true;

                if (_executor.DrainRetired())
                {
                    continue;
                }
            }

            if (WaitForEarlier(write))
            {
                return false;
            }

            // Every write admitted before it has completed.
            if (!WaitForReadsAndDrain(write))
            {
                throw write.Refusal!;
            }
        }
    }

    // Whether the refused write has to wait for writes admitted before it; then it waits, from now on
    // another thread's: the end of the last of them starts it again (Finish), or a fault or its token
    // ends it.
    private bool WaitForEarlier(PendingWrite write)
    {
        bool waits;

        lock (_lock)
        {
            waits = !_faulted && write.Older is not null;

            if (waits)
            {
                write.State = PendingWriteStateEnum.WaitingForEarlier;
            }
        }

        if (waits)
        {
            RunWaitHold();
        }

        return waits;
    }

    // For a refused write that waits for no earlier write: waits, outside every lock, for the reads
    // that hold retired memory, and frees it. False when nothing of the arena is retired, so that
    // nothing can give the write room. Its token cancels the wait.
    private bool WaitForReadsAndDrain(PendingWrite write)
    {
        var epoch = _executor.ReadsToWaitFor(out var retired);

        if (!retired)
        {
            return false;
        }

        if (epoch != 0)
        {
            RunWaitHold();
            _executor.WaitForReads(epoch, write.Token);
        }

        _executor.DrainRetired();

        return true;
    }

    private void RunWaitHold()
    {
        if (_waitHold is not null)
        {
            Interlocked.Exchange(ref _waitHold, null)?.Invoke();
        }
    }

    // Under the lock: the write goes into the queue of the commit stage.
    private void EnqueuePrepared(PendingWrite write)
    {
        if (_preparedLast is null)
        {
            _preparedFirst = write;
        }
        else
        {
            _preparedLast.NextInQueue = write;
        }

        _preparedLast = write;
    }

    // Under the lock: the next group, from the writes prepared now, in their order; false when none
    // is prepared. Its writes are dispatched from here on: the stage hands the group to the storage
    // step next, and no token is looked at any more.
    private bool FormGroup()
    {
        _group.Clear();

        while (_preparedFirst is { } write)
        {
            if (write.State == PendingWriteStateEnum.Prepared)
            {
                if (!_group.TryAdd(write.Prepared!))
                {
                    break;
                }

                write.State = PendingWriteStateEnum.Dispatched;
            }

            _preparedFirst = write.NextInQueue;
            write.NextInQueue = null;
        }

        if (_preparedFirst is null)
        {
            _preparedLast = null;
        }

        var count = _group.Writes.Count;

        if (count == 0)
        {
            return false;
        }

        if (count > _largestGroup)
        {
            Volatile.Write(ref _largestGroup, count);
        }

        if (_group.EncodedSize > _largestGroupBytes)
        {
            Volatile.Write(ref _largestGroupBytes, _group.EncodedSize);
        }

        return true;
    }

    // The commit stage, taken by this thread with a group formed (see the class): the storage step,
    // the publication, the outcomes, then the next group. On the thread of a call (own) it stops once
    // the write of the call has its outcome.
    private async ValueTask CommitAsync(PendingWrite? own)
    {
        while (true)
        {
            CommitOutcome outcome;

            try
            {
                var storing = _storage.CommitAsync(_group, CancellationToken.None);

                // The stage goes on where the storage completes, and the call returns.
                if (!storing.IsCompleted)
                {
                    own = null;
                }

                outcome = await storing.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Fault(firstSequence: 0, published: 0, exception);

                return;
            }

            var published = _executor.Publish(_group, outcome.FirstSequence, out var failure);

            if (failure is not null)
            {
                Fault(outcome.FirstSequence, published, failure);

                return;
            }

            if (!CompleteGroup(outcome.FirstSequence, own))
            {
                return;
            }
        }
    }

    // The outcomes of the writes of the group, published at consecutive sequences from the first, and
    // then the next group under the lock, unless the stage stops here: true when it goes on.
    private bool CompleteGroup(ulong firstSequence, PendingWrite? own)
    {
        var writes = _group.Writes;
        PendingWrite? started = null;

        lock (_lock)
        {
            for (var index = 0; index < writes.Count; index++)
            {
                started = Join(started, Finish(writes[index].Write));
            }
        }

        for (var index = 0; index < writes.Count; index++)
        {
            var prepared = writes[index];

            Succeed(prepared.Write, new LatticeWriteResult(_clock.At(firstSequence + (ulong)index), prepared.Kind != PublicationKindEnum.NothingToDelete));
        }

        Start(started);

        var handOff = false;
        bool goesOn;

        lock (_lock)
        {
            if (own is not null && own.State == PendingWriteStateEnum.Completed)
            {
                // The call has its outcome: it gives the stage up, and what is left goes to a caller
                // that waits for the stage, or to the thread pool.
                handOff = _preparedFirst is not null;
                goesOn = false;
            }
            else
            {
                goesOn = !_faulted && FormGroup();
            }

            _committing = goesOn;
        }

        if (handOff)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }

        return goesOn;
    }

    // A failure of the storage step or of the publication of the group (see the class, "A fault"): the
    // writes of the group before the one that failed were published at consecutive sequences from the
    // first.
    private void Fault(ulong firstSequence, int published, Exception failure)
    {
        _faultDatabase();

        var writes = _group.Writes;
        PendingWrite? ended = null;
        PendingWrite? withdrawn = null;

        lock (_lock)
        {
            _faulted = true;
            _committing = false;

            for (var index = 0; index < writes.Count; index++)
            {
                Finish(writes[index].Write);
            }

            // Every other write admitted that waits, or is prepared and waits for the commit stage. One
            // being prepared ends at the end of its preparation; one withdrawn already, by its own path.
            for (var write = _oldest; write is not null;)
            {
                var newer = write.Newer;

                switch (write.State)
                {
                    case PendingWriteStateEnum.WaitingForIdentity or PendingWriteStateEnum.WaitingToPrepare or PendingWriteStateEnum.WaitingForEarlier:
                        Finish(write);
                        write.NextToStart = ended;
                        ended = write;
                        break;

                    case PendingWriteStateEnum.Prepared:
                        write.State = PendingWriteStateEnum.Withdrawing;
                        write.NextToStart = withdrawn;
                        withdrawn = write;
                        break;
                }

                write = newer;
            }

            _readyFirst = null;
            _readyLast = null;
            _preparedFirst = null;
            _preparedLast = null;
        }

        for (var index = 0; index < writes.Count; index++)
        {
            var prepared = writes[index];

            if (index < published)
            {
                Succeed(prepared.Write, new LatticeWriteResult(_clock.At(firstSequence + (ulong)index), prepared.Kind != PublicationKindEnum.NothingToDelete));
            }
            else
            {
                End(prepared.Write, new LatticeException(
                    LatticeErrorEnum.IndeterminateOutcome,
                    "The storage step took the write, and the database faulted before it could show it: its publication, or that of a write before it in its group, failed.",
                    index == published ? failure : null));
            }
        }

        for (var write = ended; write is not null;)
        {
            var next = write.NextToStart;

            write.NextToStart = null;
            End(write, Unavailable());
            write = next;
        }

        for (var write = withdrawn; write is not null;)
        {
            var next = write.NextToStart;

            write.NextToStart = null;
            _executor.Release(write.Prepared!);

            lock (_lock)
            {
                Finish(write);
            }

            End(write, Unavailable());
            write = next;
        }
    }

    // Under the lock: the write leaves the list of the writes admitted and the chain of its identity,
    // and is completed for the coordinator. Returns the writes its end lets go on, chained by
    // NextToStart, which the caller starts after the lock (Start): the next write of its identity, and
    // the oldest write when it waits for the writes admitted before it and this was the last of them.
    // After a fault nothing goes on.
    private PendingWrite? Finish(PendingWrite write)
    {
        PendingWrite? started = null;

        var older = write.Older;
        var newer = write.Newer;

        if (older is null)
        {
            _oldest = newer;
        }
        else
        {
            older.Newer = newer;
        }

        if (newer is null)
        {
            _newest = older;
        }
        else
        {
            newer.Older = older;
        }

        write.Older = null;
        write.Newer = null;
        _queued--;

        if (older is null && newer is { State: PendingWriteStateEnum.WaitingForEarlier } && !_faulted && TakePlaceOrWait(newer, first: true))
        {
            started = newer;
        }

        var earlier = write.EarlierOfIdentity;
        var later = write.LaterOfIdentity;

        if (earlier is not null)
        {
            // A write that waited for its identity leaves the chain.
            earlier.LaterOfIdentity = later;

            if (later is not null)
            {
                later.EarlierOfIdentity = earlier;
            }
            else
            {
                _identities[new WriteIdentity(write.Table, write.Key)] = earlier;
            }
        }
        else if (later is not null)
        {
            // The identity passes to the next write of it.
            later.EarlierOfIdentity = null;

            if (!_faulted && TakePlaceOrWait(later, first: false))
            {
                later.NextToStart = started;
                started = later;
            }
        }
        else
        {
            _identities.Remove(new WriteIdentity(write.Table, write.Key));
        }

        write.EarlierOfIdentity = null;
        write.LaterOfIdentity = null;
        write.State = PendingWriteStateEnum.Completed;

        return started;
    }

    // Two chains of writes to start, as one.
    private static PendingWrite? Join(PendingWrite? chain, PendingWrite? more)
    {
        while (more is not null)
        {
            var next = more.NextToStart;

            more.NextToStart = chain;
            chain = more;
            more = next;
        }

        return chain;
    }

    // Hands every write of the chain, which has its place to be prepared, to the thread pool.
    private static void Start(PendingWrite? chain)
    {
        while (chain is not null)
        {
            var next = chain.NextToStart;

            chain.NextToStart = null;
            Schedule(chain);
            chain = next;
        }
    }

    private static void Schedule(PendingWrite? write)
    {
        if (write is not null)
        {
            ThreadPool.UnsafeQueueUserWorkItem(write, preferLocal: false);
        }
    }

    // The outcome of a write that was published: its session moves to its position, and its call
    // returns. Outside every lock; the write leaves the barrier of the close last.
    private void Succeed(PendingWrite write, LatticeWriteResult result)
    {
        try
        {
            write.Session?.Advance(result.Position);
        }
        catch (Exception exception)
        {
            End(write, exception);

            return;
        }

        write.Registration.Unregister();
        write.Succeed(result);
        _barrier.Exit();
    }

    // The failure a write ends with. Outside every lock; the write leaves the barrier of the close last.
    private void End(PendingWrite write, Exception failure)
    {
        write.Registration.Unregister();
        write.Fail(failure);
        _barrier.Exit();
    }
}
