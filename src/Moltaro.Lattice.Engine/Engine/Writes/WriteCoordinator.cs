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
//   it, and no write of the entity is prepared before the one ahead of it is published or withdrawn. A
//   write with a session is admitted once the position of its session is published (SubmitAsync): no
//   identity is reserved across that wait (brief of S09, "The order of writes and the wait of a
//   session").
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
// place to the next write waiting for one, and the end of the writes a write the arena refused waits
// for. A thread of the pool that prepared a write goes on with the next one waiting while it does
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
// writes being prepared, the writes in flight, the write being resolved, whether the commit stage is
// taken, its counters and whether the database faulted. The executor has the lock of the writes
// (WriteExecutor). The two are never held together: the coordinator calls the executor, and the
// storage step, only after it has released its own lock, and the executor takes no other lock. No
// lock is held across an await, a wait or a call of the storage step, and none is taken inside the
// protection of reads. A thread that holds the lock of the coordinator takes no other lock.
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
// entry and room in the index, for a replace or a delete a place in the retired list. A write is in
// flight while it is being prepared, prepared, withdrawn or in the commit stage; the coordinator counts
// those writes, for the resolution of a refused write.
//
// Cancellation (decision "Cancellation at the commit boundary"; brief of S09, "Dispatch" and
// "Cancellation before dispatch"). Until its dispatch the token of a write is honored at every wait —
// for its identity, for a place to be prepared, for the writes admitted before it, for the writes in
// flight in a resolution, for the reads that hold retired memory — and when its preparation ends, also
// a preparation the arena refused. A write that waits is withdrawn by the thread that cancels its
// token (Cancel); one prepared and waiting for the commit stage is withdrawn on a thread of the pool,
// so that the thread that cancels never takes the lock of the writes; one being prepared looks at its
// token when its preparation ends, and while it waits for reads, so a cancellation that comes while
// its values are encoded takes effect when the encoding ends, before it can be dispatched. A withdrawn
// write releases what it prepared, leaves the lists of the coordinator, and its call ends in
// OperationCanceledException, after nothing of it is left in QueuedWrites or in the counters of the
// engine. From the dispatch on the token is not looked at, and the call returns the real outcome.
//
// The waits of a write before its dispatch, and how each ends. The wait for the position of its
// session has the timeout of the session and the token. The waits for its identity, for a place to be
// prepared, for the writes admitted before it, for the writes in flight in a resolution and for the
// commit stage have no timeout: each has the token and ends when the writes it waits for end. The
// waits for steps of reads, for a free slot of the protection when the lookup finds every slot held
// (ReadEpochs.EnterForWrite) and for the reads that hold retired memory, have no timeout either and
// have the token; a read leaves within one step (exception X6). The lock of the coordinator and the
// lock of the writes are taken as locks, for changes in memory only.
//
// The refusal (brief of S09, "When a write is refused for memory: the rule after review round 1";
// decision Q14 as amended). A write is refused for memory only when it does not fit beside the stored
// data, never for memory other writes hold for a time or that is retired and not freed yet. A write
// the arena refuses has given back everything it prepared (WriteExecutor.TryPrepare); it frees what is
// retired and tries again. Refused again, it is resolved alone, holding its identity and no prepared
// memory:
// 1. It waits for every write admitted before it to complete (WaitingForEarlier). The end of the last
//    of them makes it the oldest write and goes on to step 2 (Finish).
// 2. It becomes the write being resolved: from then on no later write begins to be prepared — later
//    writes are admitted and wait for a place (TakePlaceOrWait, PassPlace), which is the one check the
//    ordinary path pays — and it waits for the writes in flight, all of them later than it, to complete
//    (WaitingForFlight). Each of them ends: being prepared, its preparation ends; prepared or in the
//    commit stage, the stage publishes it; refused, it waits for this write, admitted before it, and
//    holds nothing. The end of the last of them goes on to step 3 (LeaveFlight).
// 3. It waits, outside every lock, for the reads then in progress that hold retired memory to leave
//    the protection, frees what is retired, and tries once more, with no other write holding prepared
//    memory.
// 4. If the arena has room it goes on as any write; if not, it is refused with WriteRejected and Limit
//    Memory, has had no effect and never runs later. Either way the resolution ends, and the later
//    writes go on at once (EndResolution).
// Only the oldest write is resolved, so two writes refused at the same time are resolved one after the
// other in the order of their admission. The wait is finite (exception X6): the writes admitted before
// it are finite and none of them waits for it; the writes in flight when step 2 begins are finite, no
// later one begins, and each ends within its own waits; a read leaves within a step; no write
// admitted later prolongs it. Its token ends it at any step, and the later writes then go on. The
// switch of the tests runs at the first wait of the resolution of a write, whichever it is
// (HoldNextRefusedWriteWait).
//
// What a write allocates. The coordinator allocates a record for a write, PendingWrite, with the value
// source of its task and the PreparedWrite it owns, and nothing else: the identities, the lists and the
// queues are links of the records, and a write that waits is handed to the thread pool as its own work
// item. A token that can be cancelled adds its registration. A record is reused: one is kept for the
// next write when a write its call published ends, its token could not be cancelled, and the call
// returns the result itself instead of the task. Nothing refers to such a record any more then: its
// call ran the commit stage that published it and has left it (CompleteGroup), no queue, chain or list
// holds it, no registration can run, and its group is formed anew before it is read again. Every other
// write keeps its record, which its task, its registration or another thread may still read, and the
// collector takes it. The same pattern as the value-task sources the runtime reuses for its sockets.
// Measured with write-callers on the development machine, three alternating runs each: a lone caller's
// write allocates 160 bytes in the whole process instead of 488 for a replace, and 136 instead of 464
// for an insert and a delete, all of them the caller's own (the values the core takes, the task the
// scenario makes), as before S09; the collections of generation 0 in a window of 2 s fell from 94 to 129
// to 30 to 37. With 4 callers a write allocates 350 to 362 bytes instead of 507, with 16 as before, 538
// to 543, since most of their writes wait. The writes a second and the reads beside them did not move
// beyond noise.
//
// A fault (brief of S09, "Faulted" and the answers of the planner, "After a fault"). A failure of the
// storage step or of a publication faults the database: reads are refused from then on, a read that
// retries on the mark of the publication that failed gives up, and the waits for a position end
// (EngineDatabase.Fault). The writes of the group published before the failure get their results; the
// write whose publication failed and the writes of the group after it get IndeterminateOutcome, the
// first with the failure as its InnerException, and when the storage step itself failed the whole
// group does; every other write admitted gets Unavailable once it has released what it held: at once
// when it waits, at the end of its preparation when it is being prepared, whatever its preparation
// met. Every later write is Unavailable, and a close works.
internal sealed class WriteCoordinator : IThreadPoolWorkItem
{
    // How many writes are prepared at once: the starting value of the milestone contract, internal and
    // no option, since it refuses nothing (brief of S09, "PrepareWorkers is no public option"; with 64
    // places nothing was gained).
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
    // in first out. A write withdrawn while it waits here stays in the queue, completed, and is passed
    // over.
    private PendingWrite? _readyFirst;

    private PendingWrite? _readyLast;

    // The writes prepared and waiting for the commit stage, in the order their preparation ended. A
    // write withdrawn here stays in the queue and is passed over.
    private PendingWrite? _preparedFirst;

    private PendingWrite? _preparedLast;

    // The writes being prepared, at most PrepareWorkers.
    private int _preparing;

    // The writes in flight: being prepared, prepared, withdrawn or in the commit stage (see the class).
    private int _inFlight;

    // The write being resolved after the arena refused it twice, from step 2 of its resolution to its
    // end; null otherwise. While it is set no other write begins to be prepared (see the class).
    private PendingWrite? _resolving;

    // Whether a thread runs the commit stage. Read without the lock by a caller that waits for it.
    private volatile bool _committing;

    // Set once the storage step or a publication failed; never cleared.
    private volatile bool _faulted;

    // The counters (EngineCounters), written under the lock and read by the counters of the database
    // without it.
    private int _queued;

    private int _largestGroup;

    private long _largestGroupBytes;

    // A record of a write that served a write its call published, kept for the next write (see the
    // class, "What a write allocates"); null when none is kept. Taken and given back by exchange.
    private PendingWrite? _spare;

    // What the next write the arena refused runs at the first wait of its resolution, for the tests of
    // the engine (EngineDatabase.HoldNextRefusedWriteWait); null when no test armed it. Only a refused
    // write loads it.
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

    // The cancellation of the token of a write, on the thread that cancels it, which takes no lock but
    // that of the coordinator: a write that waits ends at once; one prepared and waiting for the commit
    // stage is withdrawn on a thread of the pool, which gives back what it prepared under the lock of the
    // writes (Withdraw); one being prepared looks at its token itself, and one dispatched is not touched.
    public void Cancel(PendingWrite write)
    {
        PendingWrite? started = null;
        var end = false;
        var withdraw = false;

        lock (_lock)
        {
            switch (write.State)
            {
                case PendingWriteStateEnum.WaitingForIdentity or PendingWriteStateEnum.WaitingToPrepare
                    or PendingWriteStateEnum.WaitingForEarlier or PendingWriteStateEnum.WaitingForFlight:
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
            ThreadPool.UnsafeQueueUserWorkItem(write, preferLocal: false);
        }

        if (end)
        {
            End(write, new OperationCanceledException(write.Token));
            Start(started);
        }
    }

    // A write handed to the thread pool (PendingWrite.Execute): prepared, once it holds its identity and
    // a place among the writes being prepared, or withdrawn, once its token was cancelled while it was
    // prepared (Cancel).
    public void Run(PendingWrite write)
    {
        if (write.State == PendingWriteStateEnum.Withdrawing)
        {
            Withdraw(write, new OperationCanceledException(write.Token));
        }
        else
        {
            RunPreparation(write, inline: false);
        }
    }

    // Arms the switch of the tests: the next write the arena refused runs hold once, at the first wait of
    // its resolution (EngineDatabase.HoldNextRefusedWriteWait).
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

    // Whether a write in this state is in flight (see the class).
    private static bool IsInFlight(PendingWriteStateEnum state) =>
        state is PendingWriteStateEnum.Preparing or PendingWriteStateEnum.Prepared or PendingWriteStateEnum.Withdrawing or PendingWriteStateEnum.Dispatched;

    // A write with a session whose position is not published yet: admitted once it is, so it takes its
    // place among the writes of its entity when the wait ends.
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
    // once. After a fault a write is refused here, so that no write enters the lists of the coordinator
    // after the fault has ended those in them; the preparation of a write admitted before the fault ends
    // in Unavailable at its end (PrepareOne).
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

        var write = Interlocked.Exchange(ref _spare, null) ?? new PendingWrite(this);
        var prepareHere = false;

        write.Begin(table, kind, key, values, expectedRevision, session, cancellationToken);
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
                    prepareHere = TakePlaceOrWait(write);
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

            // A write its call published, whose token cannot be cancelled, is referred to by nothing
            // any more (see the class): the call returns its result, and the record serves the next
            // write.
            if (!cancellationToken.CanBeCanceled && write.TryTakeResult(out var result))
            {
                write.Reset();
                Volatile.Write(ref _spare, write);

                return new ValueTask<LatticeWriteResult>(result);
            }
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
    // or a place in the queue of those that wait for one. No write but the one being resolved begins to
    // be prepared while a refused write is resolved: the one check the ordinary path pays for a refusal.
    // True when it has a place to be prepared.
    private bool TakePlaceOrWait(PendingWrite write)
    {
        if (_preparing < PrepareWorkers && _resolving is null)
        {
            BeginPreparation(write);

            return true;
        }

        write.State = PendingWriteStateEnum.WaitingToPrepare;

        if (_readyFirst is null)
        {
            _readyFirst = write;
        }
        else
        {
            _readyLast!.NextInQueue = write;
        }

        _readyLast = write;

        return false;
    }

    // Under the lock: the write takes a place and is in flight.
    private void BeginPreparation(PendingWrite write)
    {
        _preparing++;
        _inFlight++;
        write.State = PendingWriteStateEnum.Preparing;
    }

    // Under the lock: the next write that waits for a place, taken out of the queue; null when none
    // does.
    private PendingWrite? NextReady()
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
                return ready;
            }
        }

        return null;
    }

    // Under the lock, at the end of a preparation: its place passes to the next write that waits for
    // one, which is returned to be prepared, or is given back, as it is while a refused write is
    // resolved.
    private PendingWrite? PassPlace()
    {
        if (_resolving is null && NextReady() is { } ready)
        {
            _inFlight++;
            ready.State = PendingWriteStateEnum.Preparing;

            return ready;
        }

        _preparing--;

        return null;
    }

    // Under the lock: a write leaves flight, and when it was the last of the writes a resolved write
    // waits for, that write goes on to step 3 of its resolution with a place, every place being free
    // then. Returns it, to be started after the lock.
    private PendingWrite? LeaveFlight()
    {
        _inFlight--;

        if (_inFlight == 0 && _resolving is { State: PendingWriteStateEnum.WaitingForFlight } resolved)
        {
            BeginPreparation(resolved);

            return resolved;
        }

        return null;
    }

    // Under the lock: the write, refused twice and now the oldest write, becomes the write being
    // resolved (step 2, see the class). True when it goes on to step 3 at once, holding the place it
    // has; false when it waits for the writes in flight, after it left flight itself.
    private bool BeginResolution(PendingWrite write)
    {
        _resolving = write;

        if (_inFlight == 1)
        {
            return true;
        }

        _inFlight--;
        write.State = PendingWriteStateEnum.WaitingForFlight;

        return false;
    }

    // Under the lock, when the write being resolved is prepared or ends: no write is held back any more,
    // and those that wait for a place take the places that are free. Returns them, to be started after
    // the lock; none after a fault.
    private PendingWrite? EndResolution()
    {
        _resolving = null;

        PendingWrite? started = null;

        while (!_faulted && _preparing < PrepareWorkers && NextReady() is { } ready)
        {
            BeginPreparation(ready);
            ready.NextToStart = started;
            started = ready;
        }

        return started;
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
    // withdrawn, or ends, or waits in its resolution. Returns the write the place passed to, if any.
    private PendingWrite? PrepareOne(PendingWrite write, out bool commit, out bool waitsForStage)
    {
        var prepared = default(PreparedWrite);
        var failure = default(Exception);
        var waits = false;

        try
        {
            waits = !TryPrepare(write, out prepared);
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
            // A write that waits in its resolution is another thread's from the moment it waits
            // (TryPrepare): nothing of it is touched here but its place, given back.
            if (waits)
            {
                next = PassPlace();
            }
            else if (failure is not null)
            {
                next = PassPlace();

                // After a fault a write being prepared did not run: Unavailable, whatever it met.
                if (_faulted)
                {
                    failure = Unavailable();
                }

                started = Finish(write);
            }
            else if (_faulted || write.Token.IsCancellationRequested)
            {
                next = PassPlace();
                write.Prepared = prepared;
                write.State = PendingWriteStateEnum.Withdrawing;
                failure = _faulted ? Unavailable() : new OperationCanceledException(write.Token);
                withdraw = true;
            }
            else
            {
                // A write resolved and prepared: the writes it held back go on at once.
                if (ReferenceEquals(_resolving, write))
                {
                    started = EndResolution();
                }

                next = PassPlace();
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

        if (withdraw)
        {
            Withdraw(write, failure!);
        }
        else if (failure is not null)
        {
            End(write, failure);
        }

        Start(started);

        return next;
    }

    // The preparation of a write with the refusal of the arena (see the class): true when it is
    // prepared, false when it waits in its resolution and is another thread's from then on. A failure,
    // the final refusal and the cancellation of its token among them, passes through, with nothing of the
    // write left in the engine.
    private bool TryPrepare(PendingWrite write, out PreparedWrite? prepared)
    {
        // A write being resolved goes on here at step 3 when it is started again (LeaveFlight, Finish):
        // the coordinator set it under its lock before it handed the write over.
        if (!ReferenceEquals(_resolving, write))
        {
            if (_executor.TryPrepare(write, out prepared, out _))
            {
                return true;
            }

            // The preparation ended refused, with everything it took given back: its token is honored
            // here.
            write.Token.ThrowIfCancellationRequested();

            // Refused once: what is retired is freed, and the write tries again.
            _executor.DrainRetired();

            if (_executor.TryPrepare(write, out prepared, out _))
            {
                return true;
            }

            write.Token.ThrowIfCancellationRequested();

            if (WaitsInResolution(write))
            {
                return false;
            }
        }

        // Step 3: alone, after the reads then in progress that hold retired memory have left, once.
        WaitForReadsAndDrain(write);

        if (_executor.TryPrepare(write, out prepared, out var refusal))
        {
            return true;
        }

        throw refusal!;
    }

    // A write refused twice (see the class): waits for the writes admitted before it (step 1), or
    // becomes the write being resolved and waits for the writes in flight (step 2), or goes on to step 3
    // at once, holding its place; true when it waits, and from then on the write is another thread's,
    // so nothing of it is touched after the lock. A wait is not begun after a fault, which ended every
    // write that waited, nor once its token is cancelled, because nothing would end it then; the write
    // ends with Unavailable or OperationCanceledException instead.
    private bool WaitsInResolution(PendingWrite write)
    {
        var waits = false;
        var offer = false;
        bool faulted;
        bool cancelled;
        PendingWrite? started = null;

        lock (_lock)
        {
            faulted = _faulted;
            cancelled = write.Token.IsCancellationRequested;

            if (!faulted && !cancelled)
            {
                if (write.Older is not null)
                {
                    write.State = PendingWriteStateEnum.WaitingForEarlier;
                    started = LeaveFlight();
                    waits = true;
                }
                else
                {
                    waits = !BeginResolution(write);
                }

                // The switch of the tests runs at the first wait of the resolution alone.
                if (waits)
                {
                    offer = !write.Waited;
                    write.Waited = true;
                }
            }
        }

        if (faulted)
        {
            throw Unavailable();
        }

        if (cancelled)
        {
            throw new OperationCanceledException(write.Token);
        }

        Start(started);

        if (offer)
        {
            RunWaitHold();
        }

        return waits;
    }

    // Step 3 of a resolution: waits, outside every lock, for the reads that hold retired memory, and
    // frees it. Its token cancels the wait.
    private void WaitForReadsAndDrain(PendingWrite write)
    {
        var epoch = _executor.ReadsToWaitFor(out var retired);

        if (!retired)
        {
            return;
        }

        if (epoch != 0)
        {
            OfferWaitHold(write);
            _executor.WaitForReads(epoch, write.Token);
        }

        _executor.DrainRetired();
    }

    // The switch of the tests, at the first wait of the resolution of a write: here at the wait for
    // reads, on the thread that resolves it, which alone touches it then.
    private void OfferWaitHold(PendingWrite write)
    {
        if (!write.Waited)
        {
            write.Waited = true;
            RunWaitHold();
        }
    }

    private void RunWaitHold()
    {
        if (_waitHold is not null)
        {
            Interlocked.Exchange(ref _waitHold, null)?.Invoke();
        }
    }

    // A prepared write withdrawn before its dispatch, whose state is Withdrawing: gives back what it
    // prepared under the lock of the writes, leaves the coordinator, and ends with the failure.
    private void Withdraw(PendingWrite write, Exception failure)
    {
        _executor.Release(write.Prepared!);

        PendingWrite? started;

        lock (_lock)
        {
            started = Finish(write);
        }

        End(write, failure);
        Start(started);
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

            // The write of the call that runs the stage, which the call may take the result of itself.
            if (ReferenceEquals(prepared.Write, own))
            {
                own.PublishedByItsCall = true;
            }

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
            // Nothing goes on from here: no write is started again, and none is resolved.
            _faulted = true;
            _committing = false;
            _resolving = null;

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
                    case PendingWriteStateEnum.WaitingForIdentity or PendingWriteStateEnum.WaitingToPrepare
                        or PendingWriteStateEnum.WaitingForEarlier or PendingWriteStateEnum.WaitingForFlight:
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
                    "The storage step took the write, and the database faulted before it could show it: its publication, or that of a write before it in its group, failed, or the storage step itself failed.",
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
            Withdraw(write, Unavailable());
            write = next;
        }
    }

    // Under the lock: the write leaves the list of the writes admitted and the chain of its identity,
    // and flight when it was in flight, and is completed for the coordinator. Returns the writes its end
    // lets go on, chained by NextToStart, which the caller starts after the lock (Start): the next write
    // of its identity; the oldest write when it waits for the writes admitted before it and this was the
    // last of them, which goes on to step 2 of its resolution; the write being resolved when this was the
    // last write in flight it waited for; the writes held back when this was the write being resolved.
    // After a fault nothing goes on.
    private PendingWrite? Finish(PendingWrite write)
    {
        PendingWrite? started = null;

        if (IsInFlight(write.State))
        {
            started = LeaveFlight();
        }

        if (ReferenceEquals(_resolving, write))
        {
            started = Join(started, EndResolution());
        }

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

        // The oldest write, refused twice, waited for this one: it is resolved now (step 2).
        if (older is null && newer is { State: PendingWriteStateEnum.WaitingForEarlier } && !_faulted)
        {
            _inFlight++;

            if (BeginResolution(newer))
            {
                // Nothing else is in flight, so every place is free: it takes one.
                _preparing++;
                newer.State = PendingWriteStateEnum.Preparing;
                newer.NextToStart = started;
                started = newer;
            }
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

            if (!_faulted && TakePlaceOrWait(later))
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
