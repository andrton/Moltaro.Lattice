using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// The write coordinator of a master (brief of Epic 1, sprint S09, part 2, "The path, decided after the
// study"; milestone contract, "Write coordinator and revision semantics"). Writes are executed one at a
// time under one lock, the lock of the writes, with a group commit at the storage boundary. The path
// follows published designs, taken as ideas and not as code: the single writer of LMDB, which runs
// every write under one writer mutex and checks its conditions against the current state
// (libraries/liblmdb/mdb.c, mdb_txn_begin and mdb_txn_commit), and the group commit of the commit
// pipeline of Pebble (commit.go: the order and the log record under one mutex, publication in order)
// and of PostgreSQL (xlog.c, XLogFlush: whoever finds the log free writes for everyone queued behind
// it), with the bounds of the write group of RocksDB (db/write_thread.cc). The study of S09, part 2
// names the commits read.
//
// One write, when the storage step completes at once, as in MemoryOnly. Its call takes the lock once.
// Under it the write is admitted, prepared in place (WriteExecutor.TryPrepare: lookup, condition,
// encoding, reservations), handed to the storage step as a group of one (ICommitStorage.CommitAsync,
// called under the lock: it only takes the group), and, since that task is complete, published in the
// same hold. The call releases the lock and returns the result itself: no other thread touches the
// write, and nothing is allocated for it (see "What a write allocates").
//
// When the storage step is busy. A group whose storage step did not complete at once is in the
// storage step until its task completes; the writes prepared meanwhile wait in the queue of the
// prepared writes, and their calls return their tasks. The thread on which the storage completes takes
// the lock, publishes the group in its order, gives its writes their outcomes, prepares the writes that
// can be prepared now (below), and hands the next group to the storage step: at most
// CommitGroup.MaxOperations writes or CommitGroup.MaxEncodedSize bytes from the head of the queue, a
// larger write alone, never waiting to fill. Whoever holds the lock and finds no group in the storage
// step and prepared writes in the queue hands them over before it releases the lock (Drive), so no
// prepared write is ever left with nobody to dispatch it: either a group is in the storage step, whose
// completion will dispatch the queue, or the queue is empty when the lock is released. The writes of a
// group take consecutive positions from CommitOutcome.FirstSequence, in its order.
//
// The order of writes to one entity, and the wait behind an unpublished write. A call takes its place
// in the order when it takes the lock: a call made after an earlier call returned its task takes the
// lock after it, and the earlier write was by then published, or prepared and in the queue or in the
// storage step, or waiting in a chain, in the held list or in a resolution. A write whose entity has
// such an earlier write, one that has not completed, waits behind the latest of them, first in first
// out, in the chain of that entity (PendingWrite.EarlierOfEntity, LaterOfEntity), and holds nothing; so
// it is prepared only once every earlier write of its entity has completed, and its condition is
// checked against the state they left. Those writes are found in the list of the writes admitted that
// waited (FindLastOfEntity, and "The refusal" below); when that list is empty, as in MemoryOnly
// except while the writes a resolution held back are started, nothing is looked at and no key is
// hashed for it. The wait ends: the write it waits
// behind completes — published when its storage step completes (the timeout of that step belongs to
// S27), refused, cancelled, failed, or ended by a fault — and every way a write completes starts the
// next write of its chain under the same lock (AdvanceChain). A write with a session takes its place
// when the wait for its position ends (brief of S09, "The order of writes and the wait of a session").
// It enters the barrier of the close before that wait, so that a close waits for it as for every write
// admitted; the close ends the wait first (EngineDatabase.CloseAsync, PositionClock.Close).
//
// The lock of the writes, given by the database, which takes it as well for its counters and its
// drain, and for the start, the end and the acknowledgments of the children of a master
// (Children.Capture). It protects the arena, the table of root entries, the primary indexes, the
// position clock, the retired list (WriteExecutor) and the tail of the children (Delivery.DeliveryTail),
// and the state of this class: the queue of the prepared writes, the
// group in the storage step, the chains, the list of the writes admitted that waited, the held list,
// the write being resolved, the admission number, whether the database faulted, and the counters. It
// is held for work in memory only: no I/O, no await, no code of the application, no wait for anything
// but the lock itself. The storage step is called under it and only takes the group (ICommitStorage).
// It is the only lock of the writes, and a thread that holds it takes no other lock of the database:
// what a hold decided for other threads — the fault of the database, the outcomes of their writes, the
// end of a resolution — is carried out after the lock is released (Finish). A read takes none. A write
// holds the lock while its values are copied into the arena, so one write of a large entity holds
// every other write back for as long as its copy takes; reads are not held back. The lock is not fair:
// a caller that releases it and takes it again at once may pass a caller that waited. A gate that lets
// the callers take it first in first out was measured in S09, part 2 and not kept: it bounded the wait
// of a small write behind large ones to one large write, but small writes then went no faster than the
// large ones, and with more threads than processors, 16 writing callers beside 16 readers, the writes
// all but stopped. What is kept against the unfair hand-over is narrow: a write larger than
// CommitGroup.MaxEncodedSize, the bound above which a write forms a group alone, gives its processor
// up once (Thread.Yield) after its call released the lock, so that a caller the release woke can take
// the lock before the large caller takes it again for its next write. It makes that pass rarer and
// does not bound it: Thread.Yield gives the processor only to a thread ready on the same one, and the
// lock lets arriving callers pass a waiting one until that one has waited 100 ms
// (System.Threading.Lock). Measured in S09, part 2, with and without it alternated on a machine with
// other load, beside a caller that writes entities of 32 MiB one after another: the p99 of the small
// writes of another caller was 98 to 115 ms in 12 of 14 rounds without it, a few µs in the other two,
// and with it below 18 ms in 10 of 15 rounds and 24 to 59 ms in the others; the maximum stayed at 110
// to 125 ms either way. The series of ordinary writes did not move, since a write pays one comparison
// of its size for it.
//
// Why the hand-overs are ordered (the .NET memory model). Everything a write is and has passes between
// threads only under the lock of the writes, which releases what a thread wrote before it and acquires
// it on the thread that takes it next; the stores of a publication have release semantics
// (WriteExecutor), so a read that sees them sees the blocks. The outcome of a write that another
// thread completes is written under the lock and given after it, by its value source.
//
// Cancellation (decision "Cancellation at the commit boundary"; brief of S09, "Dispatch" and
// "Cancellation before dispatch"). Until its dispatch, the call of ICommitStorage.CommitAsync for its
// group, the token of a write is honored at every wait — chained, held, prepared and in the queue,
// waiting in a resolution for the writes admitted before it, for the writes in flight or for the reads
// that hold retired memory — and when its preparation ends, also a preparation the arena refused. The
// cancellation of a write that waits in a list or in a resolution hands its withdrawal to the thread
// pool (PendingWrite.Execute, Withdraw), so that the thread that cancels never takes the lock of the
// writes: there it leaves its list, gives back what it prepared, starts the next write of its chain,
// and its call ends in OperationCanceledException. The coordinator never makes a cancelled write run
// before that thread of the pool runs: a prepared write whose token is cancelled is withdrawn when a
// group is formed instead of being taken into it (FormGroup), as the work item would withdraw it, and
// a write that waited in a chain or in the held list is ended instead of being prepared when its turn
// comes (Start); either way the next write of its entity starts in the same hold, and the work item
// finds the write ended and does nothing. A write whose resolution waits for reads looks at its token
// in that wait itself. From the dispatch on the token is not looked at, and the call returns the real
// outcome.
//
// The refusal (brief of S09, "When a write is refused for memory: the rule after review round 1";
// decision Q14 as amended; brief of S09, part 2, "The path, decided after the study"). A write is
// refused for memory only when it does not fit beside the stored data, never for memory other writes
// hold for a time or that is retired and not freed yet. A write the arena refuses has given back
// everything it prepared (WriteExecutor.TryPrepare); it frees what is retired and tries again. Refused
// again, it is resolved alone, holding nothing:
// 1. It waits for every write admitted before it to complete (WaitingForEarlier). The writes that
//    concern it are those of the list of the writes admitted that waited (_oldest, PendingWrite.Older):
//    a write enters that list when its call leaves the lock with the write not completed, and leaves it
//    when it completes, so the list is in the order of admission, and a write that completed in the
//    hold of its call, which no other write could wait for, never enters it. While it waits in step 1,
//    later writes go on as any write. The end of the last write before it makes it the oldest and goes
//    on to step 2 (Complete).
// 2. It becomes the write being resolved (_resolving). From then on no write admitted after it begins
//    to be prepared: a later write whose entity has an earlier write that has not completed waits in
//    its chain as any write does, and every other later write waits in the held list, first in first
//    out. It waits while any
//    write is prepared and not published (Resolving): in the queue or in the group, all of them
//    later than it, prepared before it became the oldest. Each of them ends: a write that holds the
//    lock hands the queue over, and the storage step completes. When none is left the wait ends (end of
//    Drive).
// 3. Then, on a thread of its own outside every lock — the thread of its call when it never had to wait
//    before, otherwise a thread of the pool (EndResolution) — it waits for the reads then in progress
//    that hold retired memory to leave the protection, frees what is retired, and tries once more, with
//    no other write holding prepared memory.
// 4. If the arena has room it goes on as any write; if not, it is refused with WriteRejected and Limit
//    Memory, has had no effect and never runs later. Either way the resolution ends, and the held
//    writes start at once, in their order.
// In MemoryOnly a write completes in the hold of its call, so a refused write is the oldest and nothing
// is prepared and unpublished, and the resolution is the path of S08: free what is retired, wait for
// the reads that hold it, retry, refuse. The exception is the end of a resolution: the writes it held
// back are prepared in one hold and published as one group, and one of them that the arena refuses
// there may find writes before it not completed and writes prepared beside it, and goes through steps
// 1 and 2 as in a database whose storage step is busy. Only the oldest write is resolved, so two
// writes refused at the same time are resolved one after the other in the order of their admission.
// The wait is finite (exception X6): the writes admitted before it are finite and none of them waits
// for it; the writes it waits for in step 2 are finite, no later one begins, and each ends with its
// storage step; a read leaves within a step; no write admitted later prolongs it. Its token ends it at
// any step, and the held writes then go on. The switch of the tests runs at the first wait of the
// resolution of a write, whichever it is (HoldNextRefusedWriteWait).
//
// The waits of a write before its dispatch, and how each ends. The wait for the position of its
// session has the timeout of the session and the token, and the close ends it. The wait for the lock of
// the writes is that of a lock: it ends when the holder releases it, after work in memory alone. The
// waits in a chain, in the held list, in the queue of the prepared writes and in steps 1 and 2 of a
// resolution have no timeout and have the token; each ends when the writes it waits for end, as above.
// The wait for the reads that hold retired memory has no timeout and has the token; a read leaves within
// one step (exception X6).
//
// What a write allocates. A write prepared and published in the hold of its call uses the record the
// coordinator keeps for the next write (_spare), which only the holder of the lock takes and gives
// back: no other thread and no registration ever saw it, so it serves again at once, and the call
// returns its result as a value. A write that waits keeps a record of its own, allocated then, with the
// value source of its task, and the collector takes it once its task completed; a token that can be
// cancelled adds its registration. A storage step that does not complete at once adds the task of its
// completion.
//
// Children (brief of Epic 1, sprint S11; the planner's decision of 17 November 2026 after the study,
// points 2 and 5). A master publishes every write while children are connected or loading; no write is
// refused or delayed because of a child. The capture of a child is registered in a hold of the lock of
// the writes (Children.Capture), and a write published in a later hold is appended to the tail of the
// children in the hold that publishes it (WriteExecutor), so a write is either published before the
// capture starts or delivered after it, whichever hold its preparation was in; the senders of the
// children are woken after the hold (Finish). A child admits no write here: it forwards its writes to its
// master (Forwarding.Forwarder).
//
// Forwarded writes (brief of Epic 1, sprint S12; the planner's decisions after the study, points 1, 2 and
// 12, and decided at the review, point 1). The receiver of a child's connection submits each write the
// child forwarded, in the order it received them, without awaiting one before it submits the next
// (Children.ChildConnection, Forward), so a forwarded write takes its place among the writes of its entity
// as any write does. It has no token: a request received whole is a submitted write and runs although its
// child's connection ends. It brings the root the child sent instead of values (ForwardedOrigin), which the
// executor places when it prepares the write. It has no caller: its outcome is its reply, recorded for the
// child before the write leaves the barrier of the close, whatever the outcome. A success is recorded in
// the hold that publishes it, before its transaction is appended to the tail (WriteExecutor), so that the
// child takes the reply before the transaction; a refusal where its outcome is given, after the hold that
// decided it (Deliver, Outcome), or at once when it is refused before its admission. So once the barrier
// has drained, every forwarded write admitted has its reply recorded, and nothing of one runs any more: no
// task of it is awaited.
//
// A fault (brief of S09, "Faulted" and the answers of the planner, "After a fault"). A failure of the
// storage step or of a publication faults the database: reads are refused from then on, a read that
// retries on the mark of the publication that failed gives up, and the waits for a position end
// (EngineDatabase.Fault). The writes of the group published before the failure get their results; the
// write whose publication failed and the writes of the group after it get IndeterminateOutcome, the
// first with the failure as its InnerException, and when the storage step itself failed the whole
// group does; every other write admitted gets Unavailable once it has released what it held. Every
// later write is Unavailable, and a close works.
internal sealed class WriteCoordinator
{
    // The lock of the writes (see the class).
    private readonly Lock _writes;

    private readonly WriteExecutor _executor;

    private readonly ICommitStorage _storage;

    private readonly CloseBarrier _barrier;

    private readonly PositionClock _clock;

    // How long a write waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    // Faults the database (EngineDatabase): its state, its reads and its waits for a position.
    private readonly Action _faultDatabase;

    // The group handed to the storage step, formed anew for every dispatch.
    private readonly CommitGroup _group = new();

    // The writes prepared and not handed to the storage step yet, in the order they were prepared.
    private PendingWrite? _preparedFirst;

    private PendingWrite? _preparedLast;

    // Whether the group holds writes, which are prepared and not published: from the first write
    // FormGroup takes into it, through the storage step, until it is published or the database faults.
    private bool _groupPending;

    // The writes admitted and not completed that waited, in the order of their admission (see the
    // class, "The refusal").
    private PendingWrite? _oldest;

    private PendingWrite? _newest;

    // The writes held back while a refused write is resolved, in the order they were held.
    private PendingWrite? _heldFirst;

    private PendingWrite? _heldLast;

    // The write being resolved after the arena refused it twice, from step 2 of its resolution to its
    // end; null otherwise (see the class).
    private PendingWrite? _resolving;

    // The number of the last write admitted.
    private long _admitted;

    // Set once the storage step or a publication failed; never cleared.
    private bool _faulted;

    // The counters (EngineCounters), written under the lock and read by the counters of the database.
    private int _queued;

    private int _largestGroup;

    private long _largestGroupBytes;

    // The record for the next write (see the class, "What a write allocates"); null when none is kept.
    private PendingWrite? _spare;

    // What the next write the arena refused runs at the first wait of its resolution, for the tests of
    // the engine (EngineDatabase.HoldNextRefusedWriteWait); on a child, what the next transaction its arena
    // refused twice runs before its applier waits for the reads (RunWaitHold). Null when no test armed it.
    // Only a refused write or transaction loads it.
    private Action? _waitHold;

    // The coordinator of a master. A child has one as well, which admits no write: its writes are
    // forwarded to its master (Forwarding.Forwarder).
    public WriteCoordinator(Lock writes, WriteExecutor executor, ICommitStorage storage, CloseBarrier barrier, PositionClock clock, TimeSpan synchronizationTimeout, Action faultDatabase)
    {
        _writes = writes;
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
    // engine does not store; ObjectDisposedException after the close; for the session, HistoryMismatch,
    // SynchronizationTimeout and Unavailable; Unavailable after a fault; RevisionConflict, DuplicateKey
    // and NotFound for its condition; WriteRejected, with Limit Memory, for an entity the memory budget
    // cannot hold; IndeterminateOutcome when a publication failed after the storage step took it.
    // ValidationFailed for text that is not well-formed or a value above the bound of one value comes
    // from the guard of the root writer, only for values that the validation of the core did not check.
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

        if (!TryEnter(session, cancellationToken, out var waited, out var refusal))
        {
            return ValueTask.FromException<LatticeWriteResult>(refusal);
        }

        // On a master the position of a session is published already, unless a caller made it up.
        if (!waited.IsCompletedSuccessfully)
        {
            return AdmitAfterAsync(waited, table, kind, key, values, expectedRevision, session, cancellationToken);
        }

        return Admit(table, kind, key, values, forwarded: null, expectedRevision, session, cancellationToken);
    }

    // Submits a write a child forwarded, of an entity type the engine stores, which brings its root and
    // where it came from instead of values (see the class, "Forwarded writes"). Its outcome is its reply,
    // with the refusals of SubmitAsync, and is recorded before the write leaves the barrier of the close;
    // nothing is returned, and the task of its admission is not looked at.
    public void Forward(EntityTable table, WriteKindEnum kind, in EntityKey key, ForwardedOrigin forwarded, LatticeRevision? expectedRevision, LatticeSession? session)
    {
        if (!TryEnter(session, CancellationToken.None, out var waited, out var refusal))
        {
            forwarded.Refuse(refusal);

            return;
        }

        if (!waited.IsCompletedSuccessfully)
        {
            _ = ForwardAfterAsync(waited, table, kind, key, forwarded, expectedRevision, session);

            return;
        }

        _ = Admit(table, kind, key, values: null, forwarded, expectedRevision, session, CancellationToken.None);
    }

    // Arms the switch of the tests: the next write the arena refused runs hold once, at the first wait of
    // its resolution (EngineDatabase.HoldNextRefusedWriteWait).
    public void HoldNextWait(Action hold) => Volatile.Write(ref _waitHold, hold);

    // Runs the switch of the tests once, when one is armed, outside every lock: for a write at the first
    // wait of its resolution (OfferWaitHold), and on a child, whose coordinator admits no write, for a
    // transaction its arena refused twice, once its applier knows it waits for the reads that hold
    // retired memory and before it waits (Following.Follower).
    public void RunWaitHold()
    {
        if (_waitHold is not null)
        {
            Interlocked.Exchange(ref _waitHold, null)?.Invoke();
        }
    }

    // Arms a fault point for the tests of the engine (EngineDatabase.ArmFault).
    public void ArmFault(EngineFaultPointEnum point, int skippedPasses)
    {
        lock (_writes)
        {
            _executor.Arm(point, skippedPasses);
        }
    }

    // On a thread of the pool, once the token of a write that waits was cancelled (PendingWrite.Execute):
    // a write that waits in a list leaves it, gives back what it prepared, starts the next write of its
    // chain and ends in OperationCanceledException. A write dispatched, one whose resolution waits for
    // reads, which looks at its token itself, and one that has ended are not touched.
    public void Withdraw(PendingWrite write)
    {
        var pass = default(Pass);

        lock (_writes)
        {
            var cancelled = new OperationCanceledException(write.Token);

            switch (write.State)
            {
                case PendingWriteStateEnum.Chained:
                    Unchain(write);
                    End(write, cancelled, ref pass);
                    break;

                case PendingWriteStateEnum.Held:
                    Unlink(ref _heldFirst, ref _heldLast, write);
                    End(write, cancelled, ref pass);
                    AdvanceChain(write, ref pass);
                    break;

                case PendingWriteStateEnum.Prepared:
                    Unlink(ref _preparedFirst, ref _preparedLast, write);
                    _executor.Release(write.Prepared!);
                    End(write, cancelled, ref pass);
                    AdvanceChain(write, ref pass);
                    break;

                case PendingWriteStateEnum.WaitingForEarlier:
                    End(write, cancelled, ref pass);
                    AdvanceChain(write, ref pass);
                    break;

                case PendingWriteStateEnum.Resolving:
                    _resolving = null;
                    End(write, cancelled, ref pass);
                    AdvanceChain(write, ref pass);
                    ReleaseHeld(ref pass);
                    break;
            }

            Drive(ref pass);
        }

        Finish(ref pass);
    }

    // Step 3 of the resolution of a refused write (see the class), on a thread of the pool, once the
    // writes it waited for in step 2 were published (PendingWrite.Execute).
    public void EndResolution(PendingWrite write)
    {
        var pass = Resolve(write, own: false);

        Finish(ref pass);
    }

    private static LatticeException Unavailable() =>
        new(LatticeErrorEnum.Unavailable, "The database is Faulted: a publication failed, and it serves nothing until a new instance is opened.");

    // Takes the write out of a list linked by Next, which holds it; linear in the list (see
    // FindLastOfEntity).
    private static void Unlink(ref PendingWrite? first, ref PendingWrite? last, PendingWrite write)
    {
        PendingWrite? before = null;

        for (var current = first; current is not null; current = current.Next)
        {
            if (!ReferenceEquals(current, write))
            {
                before = current;

                continue;
            }

            if (before is null)
            {
                first = write.Next;
            }
            else
            {
                before.Next = write.Next;
            }

            if (ReferenceEquals(last, write))
            {
                last = before;
            }

            write.Next = null;

            return;
        }
    }

    private static void Append(ref PendingWrite? first, ref PendingWrite? last, PendingWrite write)
    {
        write.Next = null;

        if (last is null)
        {
            first = write;
        }
        else
        {
            last.Next = write;
        }

        last = write;
    }

    // A chained write leaves the chain of its entity; the writes behind it wait behind the one before
    // it. A write in the state Chained outside a hold of the lock always has the write before it: only
    // AdvanceChain clears it, and the write it makes ready is started in the same hold (Drive, Pass),
    // so Withdraw, the only caller, never meets a chained write without one.
    private static void Unchain(PendingWrite write)
    {
        var earlier = write.EarlierOfEntity!;
        var later = write.LaterOfEntity;

        earlier.LaterOfEntity = later;

        if (later is not null)
        {
            later.EarlierOfEntity = earlier;
        }

        write.EarlierOfEntity = null;
        write.LaterOfEntity = null;
    }

    // Enters the barrier of the close and begins the wait for the position of the session, if there is
    // one, so that the write is inside the barrier from its wait on (see the class, "The order of writes
    // to one entity"); waited completes when the position is published. False, with the refusal and
    // outside the barrier: ObjectDisposedException after the close; for the session, HistoryMismatch.
    //
    // The session is bound to the history of this database here, when the write is admitted and before
    // it runs (PositionClock.Bind; decision on session consistency, points 4 and 7): a session of another
    // history, and an empty one that another database bound first, is refused before anything is
    // executed. So the session of a write that committed is of this history, and moving it to the
    // position of the write cannot fail: the result of a commit is never replaced by a failure of the
    // session (Outcome, Deliver).
    private bool TryEnter(LatticeSession? session, CancellationToken cancellationToken, out ValueTask waited, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out Exception? refusal)
    {
        waited = default;
        refusal = null;

        try
        {
            _barrier.Enter();
        }
        catch (ObjectDisposedException exception)
        {
            refusal = exception;

            return false;
        }

        if (session is null)
        {
            return true;
        }

        try
        {
            _clock.Bind(session);
            waited = _clock.WaitAsync(session, _synchronizationTimeout, cancellationToken);

            return true;
        }
        catch (Exception exception)
        {
            _barrier.Exit();
            refusal = exception;

            return false;
        }
    }

    // A write with a session whose position is not published yet, inside the barrier: admitted once it
    // is, so it takes its place among the writes of its entity when the wait ends; a wait that fails
    // leaves the barrier with its failure.
    private async ValueTask<LatticeWriteResult> AdmitAfterAsync(
        ValueTask waited,
        EntityTable table,
        WriteKindEnum kind,
        EntityKey key,
        object?[]? values,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken cancellationToken)
    {
        try
        {
            await waited.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            _barrier.Exit();
            throw;
        }

        return await Admit(table, kind, key, values, forwarded: null, expectedRevision, session, cancellationToken).ConfigureAwait(false);
    }

    // The same for a forwarded write: a wait that fails is its refusal, recorded before it leaves the
    // barrier, and nothing of it runs after that. The task never fails, and no one awaits it.
    private async Task ForwardAfterAsync(
        ValueTask waited,
        EntityTable table,
        WriteKindEnum kind,
        EntityKey key,
        ForwardedOrigin forwarded,
        LatticeRevision? expectedRevision,
        LatticeSession? session)
    {
        try
        {
            await waited.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            forwarded.Refuse(exception);
            _barrier.Exit();

            return;
        }

        _ = Admit(table, kind, key, values: null, forwarded, expectedRevision, session, CancellationToken.None);
    }

    // The call of a write inside the barrier of the close (see the class): one hold of the lock that
    // admits it and, when nothing makes it wait, prepares, dispatches and publishes it; then the outcome,
    // or the task when it waits. The write leaves the barrier when its outcome is given.
    private ValueTask<LatticeWriteResult> Admit(
        EntityTable table,
        WriteKindEnum kind,
        in EntityKey key,
        object?[]? values,
        ForwardedOrigin? forwarded,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken cancellationToken)
    {
        var pass = default(Pass);
        PendingWrite? write = null;
        LatticeWriteResult result = default;
        Exception? failure = null;
        var large = false;

        lock (_writes)
        {
            // After a fault a write is refused here, so that none enters the lists after the fault has
            // ended those in them.
            if (_faulted)
            {
                failure = Unavailable();
            }
            else
            {
                write = _spare ?? new PendingWrite(this);
                _spare = null;
                write.Begin(table, kind, key, values, forwarded, expectedRevision, session, cancellationToken, ++_admitted);
                _queued++;
                pass.Own = write;
                Start(write, ref pass);
                Drive(ref pass);

                if (!pass.OwnEnded)
                {
                    List(write);
                }
                else
                {
                    result = write.Outcome;
                    failure = write.Failure;
                    large = write.Prepared?.EncodedSize > CommitGroup.MaxEncodedSize;

                    // Nothing refers to the record any more: no other thread saw it, and it was never
                    // registered (see the class).
                    write.ResetAfterItsCall();
                    _spare = write;
                }
            }
        }

        Finish(ref pass);

        if (write is null)
        {
            return Outcome(session, forwarded, result, failure);
        }

        // A write refused twice with nothing to wait for is resolved on the thread of its call.
        if (pass.ReadsHere is not null)
        {
            var resolved = Resolve(write, own: true);

            Finish(ref resolved);

            if (resolved.OwnEnded)
            {
                result = write.Outcome;
                failure = write.Failure;
                pass.OwnEnded = true;
            }
        }

        if (pass.OwnEnded)
        {
            // A write larger than a group gives its processor up once after it released the lock (see
            // the class, "The lock of the writes"), so that a caller woken by that release can take the
            // lock before this caller takes it again.
            if (large)
            {
                Thread.Yield();
            }

            return Outcome(session, forwarded, result, failure);
        }

        if (cancellationToken.CanBeCanceled)
        {
            Register(write);
        }

        return write.Task;
    }

    // The outcome of a write its call gives itself: its session moves to its position, which cannot fail
    // for a session bound at admission (TryEnter), the write leaves the barrier of the close, and the
    // call returns. A forwarded write has its refusal recorded as its reply before it leaves, and its
    // success was recorded by the hold that published it; its task carries nothing, since no one looks
    // at it (Forward).
    private ValueTask<LatticeWriteResult> Outcome(LatticeSession? session, ForwardedOrigin? forwarded, LatticeWriteResult result, Exception? failure)
    {
        if (forwarded is not null)
        {
            if (failure is not null)
            {
                forwarded.Refuse(failure);
            }

            _barrier.Exit();

            return default;
        }

        if (failure is null)
        {
            session?.Advance(result.Position);
        }

        _barrier.Exit();

        return failure is null ? new ValueTask<LatticeWriteResult>(result) : ValueTask.FromException<LatticeWriteResult>(failure);
    }

    // Registers the cancellation of the token of a write that waits. A token already cancelled hands its
    // withdrawal to the pool at once. The registration is kept under the lock, so that the end of the
    // write, which unregisters it, finds it, or the write has ended before and it is unregistered here.
    private void Register(PendingWrite write)
    {
        var registration = write.Token.UnsafeRegister(static state => ThreadPool.UnsafeQueueUserWorkItem((PendingWrite)state!, preferLocal: false), write);
        bool ended;

        lock (_writes)
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

    // Under the lock: the way of a write that may go on now — at its admission, at the end of the write
    // before it in its chain, or at the end of a resolution that held it back.
    private void Start(PendingWrite write, ref Pass pass)
    {
        if (_faulted)
        {
            End(write, Unavailable(), ref pass);
            AdvanceChain(write, ref pass);

            return;
        }

        // A write whose token was cancelled while it waited ends here, before anything is prepared for
        // it, whether or not the thread of the pool its cancellation went to has run.
        if (write.Token.IsCancellationRequested)
        {
            End(write, new OperationCanceledException(write.Token), ref pass);
            AdvanceChain(write, ref pass);

            return;
        }

        if (FindLastOfEntity(write) is { } last)
        {
            // The latest earlier write of the entity has no write behind it: one admitted after it would
            // be the latest.
            last.LaterOfEntity = write;
            write.EarlierOfEntity = last;
            write.State = PendingWriteStateEnum.Chained;

            return;
        }

        if (_resolving is { } resolving && write.Admission > resolving.Admission)
        {
            write.State = PendingWriteStateEnum.Held;
            Append(ref _heldFirst, ref _heldLast, write);

            return;
        }

        Prepare(write, ref pass);
    }

    // Under the lock: the latest write of the entity of the write that was admitted before it, has not
    // completed and waited: prepared and unpublished, in a chain, held, or in a resolution; null when
    // there is none. The write waits behind it. Every such write is in the list of the writes admitted
    // that waited (see the class, "The refusal"), and a write of the call that holds the lock is the only
    // one not in it yet, so the list is searched, from the newest write admitted before this one back.
    // Nothing is looked at, and no key is hashed, when the list is empty, as in MemoryOnly outside a
    // resolution. The search is linear in the writes that wait, and so is Unlink in the queue and the
    // held list: with 40,000 writes waiting behind a busy storage step an admission took 0.6 to 0.8 ms
    // in review round 1 of S09, part 2. It never runs while the storage step completes at once; whether
    // a busy durable step needs a structure that finds them faster is settled in the brief of S27.
    private PendingWrite? FindLastOfEntity(PendingWrite write)
    {
        for (var listed = write.Listed ? write.Older : _newest; listed is not null; listed = listed.Older)
        {
            if (listed.IsOfSameEntity(write))
            {
                return listed;
            }
        }

        return null;
    }

    // Under the lock: prepares the write, which has no earlier write of its entity that has not
    // completed, and puts it into the queue of the prepared writes; or ends it, or begins its
    // resolution (see the class, "The refusal").
    private void Prepare(PendingWrite write, ref Pass pass)
    {
        PreparedWrite? prepared;

        try
        {
            if (!TryPrepareTwice(write, out prepared))
            {
                BeginResolution(write, ref pass);

                return;
            }
        }
        catch (Exception exception)
        {
            End(write, exception, ref pass);
            AdvanceChain(write, ref pass);

            return;
        }

        // Its token is honored when its preparation ends, before it can be dispatched.
        if (write.Token.IsCancellationRequested)
        {
            _executor.Release(prepared);
            End(write, new OperationCanceledException(write.Token), ref pass);
            AdvanceChain(write, ref pass);

            return;
        }

        write.Prepared = prepared;
        write.State = PendingWriteStateEnum.Prepared;
        Append(ref _preparedFirst, ref _preparedLast, write);
    }

    // Under the lock: the preparation of a write with the first two tries of a refusal of the arena
    // (see the class): true when it is prepared, false when it was refused twice and holds nothing. Its
    // token is honored at the end of each refused try, with OperationCanceledException.
    private bool TryPrepareTwice(PendingWrite write, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PreparedWrite? prepared)
    {
        if (_executor.TryPrepare(write, out prepared, out _))
        {
            return true;
        }

        write.Token.ThrowIfCancellationRequested();

        // Refused once: what is retired is freed, and the write tries again.
        _executor.DrainRetired();

        if (_executor.TryPrepare(write, out prepared, out _))
        {
            return true;
        }

        write.Token.ThrowIfCancellationRequested();

        return false;
    }

    // Under the lock: the resolution of a write refused twice begins (see the class): step 1 while a
    // write admitted before it has not completed, step 2 when it is the oldest.
    private void BeginResolution(PendingWrite write, ref Pass pass)
    {
        // The write of the call that holds the lock enters the list of the writes that waited now: every
        // write in it was admitted before it.
        List(write);

        if (write.Older is not null)
        {
            write.State = PendingWriteStateEnum.WaitingForEarlier;
            Offer(write, ref pass);

            return;
        }

        BecomeResolved(write, ref pass);
    }

    // Under the lock: the oldest write, refused twice, becomes the write being resolved (step 2), and
    // goes on to step 3 at once when no write holds prepared memory.
    private void BecomeResolved(PendingWrite write, ref Pass pass)
    {
        _resolving = write;

        if (HoldsPreparedMemory())
        {
            write.State = PendingWriteStateEnum.Resolving;
            Offer(write, ref pass);

            return;
        }

        ToReads(write, ref pass);
    }

    // Under the lock: the switch of the tests is offered to a write that begins to wait in its
    // resolution, after the lock, unless it was offered at an earlier wait.
    private static void Offer(PendingWrite write, ref Pass pass)
    {
        if (!write.Waited)
        {
            pass.Offer = write;
        }
    }

    // Under the lock: the write enters the list of the writes admitted that waited, last, unless it is
    // in it.
    private void List(PendingWrite write)
    {
        if (write.Listed)
        {
            return;
        }

        write.Listed = true;
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
    }

    // Under the lock: the write leaves the list of the writes admitted that waited, when it is in it; when
    // it was the oldest and the next one waits in step 1 of its resolution, that one is resolved now.
    private void Unlist(PendingWrite write, ref Pass pass)
    {
        if (!write.Listed)
        {
            return;
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
        write.Listed = false;

        if (older is null && newer is { State: PendingWriteStateEnum.WaitingForEarlier } && !_faulted)
        {
            BecomeResolved(newer, ref pass);
        }
    }

    // Under the lock: whether a write is prepared and not published, and so holds prepared memory: one
    // is in the queue, or in the group, from the first write the group being formed took (FormGroup)
    // to its publication. Every prepared write that is not published is in one of the two, so the
    // answer is that of the whole state at every moment of a hold, also while a group is formed and a
    // write withdrawn there ends.
    private bool HoldsPreparedMemory() => _preparedFirst is not null || _groupPending;

    // Under the lock: the write being resolved goes on to step 3, which waits outside every lock: on the
    // thread of its call when that call holds the lock now, otherwise on a thread of the pool.
    private void ToReads(PendingWrite write, ref Pass pass)
    {
        write.State = PendingWriteStateEnum.Reading;

        if (ReferenceEquals(write, pass.Own))
        {
            pass.ReadsHere = write;
        }
        else
        {
            write.ResolutionPending = 1;
            pass.Scheduled = write;
        }
    }

    // Step 3 of a resolution and its end (see the class), outside the lock until it takes it for the
    // last try; for the write of the call that runs it when own is true.
    private Pass Resolve(PendingWrite write, bool own)
    {
        var pass = default(Pass);
        long epoch;
        bool retired;
        Exception? cancelled = null;

        lock (_writes)
        {
            epoch = _executor.ReadsToWaitFor(out retired);
        }

        if (retired && epoch != 0)
        {
            OfferWaitHold(write);

            try
            {
                _executor.WaitForReads(epoch, write.Token);
            }
            catch (OperationCanceledException exception)
            {
                cancelled = exception;
            }
        }

        lock (_writes)
        {
            if (own)
            {
                pass.Own = write;
            }

            _resolving = null;

            if (_faulted)
            {
                End(write, Unavailable(), ref pass);
            }
            else if (cancelled is not null)
            {
                End(write, cancelled, ref pass);
            }
            else
            {
                TryLast(write, ref pass);
            }

            if (write.State == PendingWriteStateEnum.Completed)
            {
                AdvanceChain(write, ref pass);
            }

            ReleaseHeld(ref pass);
            Drive(ref pass);
        }

        return pass;
    }

    // Under the lock: the last try of a resolved write, with no other write holding prepared memory:
    // into the queue of the prepared writes, or refused for memory.
    private void TryLast(PendingWrite write, ref Pass pass)
    {
        try
        {
            _executor.DrainRetired();

            if (_executor.TryPrepare(write, out var prepared, out var refusal))
            {
                if (write.Token.IsCancellationRequested)
                {
                    _executor.Release(prepared);
                    End(write, new OperationCanceledException(write.Token), ref pass);

                    return;
                }

                write.Prepared = prepared;
                write.State = PendingWriteStateEnum.Prepared;
                Append(ref _preparedFirst, ref _preparedLast, write);

                return;
            }

            // A token cancelled while it was tried ends it as cancelled, not refused.
            write.Token.ThrowIfCancellationRequested();
            End(write, refusal!, ref pass);
        }
        catch (Exception exception)
        {
            End(write, exception, ref pass);
        }
    }

    // Under the lock: the held writes start, in their order, as the end of a resolution lets them.
    private void ReleaseHeld(ref Pass pass)
    {
        while (_heldFirst is { } held)
        {
            _heldFirst = held.Next;
            held.Next = null;
            pass.AddReady(held);
        }

        _heldLast = null;
    }

    // Under the lock: as long as a write can go on, it goes on: the writes made ready start, and the
    // queue of the prepared writes is handed to the storage step whenever no group is in it. The loop
    // is left only when no write is ready (see Pass). At the end a resolved write that no longer has
    // anything to wait for goes on to step 3.
    private void Drive(ref Pass pass)
    {
        while (true)
        {
            if (pass.TakeReady() is { } ready)
            {
                Start(ready, ref pass);

                continue;
            }

            if (_groupPending || _faulted)
            {
                break;
            }

            if (FormGroup(ref pass))
            {
                Commit(ref pass);

                continue;
            }

            // No group was formed, and a write FormGroup withdrew may have made the next write of its
            // entity ready.
            if (!pass.HasReady)
            {
                break;
            }
        }

        if (_resolving is { State: PendingWriteStateEnum.Resolving } resolving && !HoldsPreparedMemory())
        {
            ToReads(resolving, ref pass);
        }
    }

    // Under the lock: the next group, from the head of the queue of the prepared writes; false when no
    // write was taken. A prepared write whose token is cancelled is withdrawn here instead of being
    // taken into the group, whether or not the thread of the pool its cancellation went to has run
    // (see the class, "Cancellation"); the next write of its entity becomes ready, and Drive starts
    // it. The writes taken are dispatched from here on: the group is handed to the storage step next,
    // and no token is looked at any more. From the first of them on the group holds prepared memory
    // (_groupPending), so that a write withdrawn after it, whose end may make a refused write the one
    // being resolved, does not find nothing prepared while the group holds it (HoldsPreparedMemory).
    private bool FormGroup(ref Pass pass)
    {
        _group.Clear();

        while (_preparedFirst is { } write)
        {
            if (write.Token.IsCancellationRequested)
            {
                _preparedFirst = write.Next;
                write.Next = null;
                _executor.Release(write.Prepared!);
                End(write, new OperationCanceledException(write.Token), ref pass);
                AdvanceChain(write, ref pass);

                continue;
            }

            if (!_group.TryAdd(write.Prepared!))
            {
                break;
            }

            _groupPending = true;
            write.State = PendingWriteStateEnum.Dispatched;
            _preparedFirst = write.Next;
            write.Next = null;
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

    // Under the lock: the storage step for the group formed (the dispatch), which only takes it
    // (ICommitStorage). A task already completed means the group is committed, and it is published in
    // this hold; otherwise the thread on which it completes publishes it (CompleteStorageAsync).
    private void Commit(ref Pass pass)
    {
        ValueTask<CommitOutcome> storing;

        try
        {
            storing = _storage.CommitAsync(_group, CancellationToken.None);
        }
        catch (Exception exception)
        {
            FaultGroup(0, 0, exception, ref pass);

            return;
        }

        if (!storing.IsCompleted)
        {
            _ = CompleteStorageAsync(storing.AsTask());

            return;
        }

        CommitOutcome outcome;

        try
        {
            outcome = storing.Result;
        }
        catch (Exception exception)
        {
            FaultGroup(0, 0, exception, ref pass);

            return;
        }

        PublishGroup(outcome.FirstSequence, ref pass);
    }

    // A storage step that did not complete at once: its group is published on the thread on which it
    // completes, never on the thread that dispatched it, which holds the lock and goes on.
    private async Task CompleteStorageAsync(Task<CommitOutcome> storing)
    {
        CommitOutcome outcome = default;
        Exception? failure = null;

        try
        {
            outcome = await storing.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var pass = default(Pass);

        lock (_writes)
        {
            if (failure is null)
            {
                PublishGroup(outcome.FirstSequence, ref pass);
            }
            else
            {
                FaultGroup(0, 0, failure, ref pass);
            }

            Drive(ref pass);
        }

        Finish(ref pass);
    }

    // Under the lock: publishes the group at consecutive sequences from the first, gives its writes
    // their outcomes, and starts the next write of the chain of each. When a publication failed the
    // database faults first, before a write of the group completes, so that the end of one of them
    // begins no resolution while the writes of the group after the failure still hold prepared memory.
    private void PublishGroup(ulong firstSequence, ref Pass pass)
    {
        var published = _executor.Publish(_group, firstSequence, out var failure);
        var writes = _group.Writes;

        pass.Published = true;
        pass.Delivered |= _executor.TakeDelivered();

        if (failure is not null)
        {
            FaultGroup(firstSequence, published, failure, ref pass);
        }

        _groupPending = false;

        for (var index = 0; index < published; index++)
        {
            var prepared = writes[index];

            Succeed(prepared.Write, new LatticeWriteResult(_clock.At(firstSequence + (ulong)index), prepared.Kind != PublicationKindEnum.NothingToDelete), ref pass);
            AdvanceChain(prepared.Write, ref pass);
        }
    }

    // Under the lock: a failure of the storage step of the group, or of the publication of its write at
    // the index published (see the class, "A fault"): the writes of the group from that index on are
    // IndeterminateOutcome, and every other write admitted ends with Unavailable, once it has released
    // what it held.
    private void FaultGroup(ulong firstSequence, int published, Exception failure, ref Pass pass)
    {
        _faulted = true;
        _groupPending = false;
        pass.Faulted = true;

        var writes = _group.Writes;

        for (var index = published; index < writes.Count; index++)
        {
            var write = writes[index].Write;

            End(write, new LatticeException(
                LatticeErrorEnum.IndeterminateOutcome,
                "The storage step took the write, and the database faulted before it could show it: its publication, or that of a write before it in its group, failed, or the storage step itself failed.",
                index == published ? failure : null), ref pass);
            AdvanceChain(write, ref pass);
        }

        while (_preparedFirst is { } prepared)
        {
            _preparedFirst = prepared.Next;
            prepared.Next = null;
            _executor.Release(prepared.Prepared!);
            End(prepared, Unavailable(), ref pass);
            AdvanceChain(prepared, ref pass);
        }

        _preparedLast = null;

        if (_resolving is { State: PendingWriteStateEnum.Resolving } resolving)
        {
            _resolving = null;
            End(resolving, Unavailable(), ref pass);
            AdvanceChain(resolving, ref pass);
        }

        for (var listed = _oldest; listed is not null;)
        {
            var newer = listed.Newer;

            if (listed.State == PendingWriteStateEnum.WaitingForEarlier)
            {
                End(listed, Unavailable(), ref pass);
                AdvanceChain(listed, ref pass);
            }

            listed = newer;
        }

        // The held writes and the writes of every chain start, and end with Unavailable (Start).
        ReleaseHeld(ref pass);
    }

    // Under the lock: the next write of the chain of the write, which has ended or is published, starts.
    private static void AdvanceChain(PendingWrite write, ref Pass pass)
    {
        if (write.LaterOfEntity is { } later)
        {
            write.LaterOfEntity = null;
            later.EarlierOfEntity = null;
            pass.AddReady(later);
        }
    }

    // Under the lock: the write succeeded, and its outcome is given after the lock.
    private void Succeed(PendingWrite write, LatticeWriteResult result, ref Pass pass)
    {
        write.Outcome = result;
        write.Failure = null;
        Complete(write, ref pass);
    }

    // Under the lock: the write ends with the failure, given after the lock.
    private void End(PendingWrite write, Exception failure, ref Pass pass)
    {
        write.Failure = failure;
        Complete(write, ref pass);
    }

    private void Complete(PendingWrite write, ref Pass pass)
    {
        write.State = PendingWriteStateEnum.Completed;
        _queued--;
        Unlist(write, ref pass);

        if (ReferenceEquals(write, pass.Own))
        {
            pass.OwnEnded = true;
        }
        else
        {
            pass.AddEnded(write);
        }
    }

    // After the lock: what the hold decided. The database faults first, so that the reads give up; the
    // waits for a position are woken after a publication; every other write that ended gets its
    // outcome; a resolution that goes on to step 3 goes to the thread pool; the switch of the tests runs
    // for a write that begins to wait in its resolution.
    private void Finish(ref Pass pass)
    {
        if (pass.Faulted)
        {
            _faultDatabase();
        }

        if (pass.Published)
        {
            _executor.WakePositions();
        }

        if (pass.Delivered)
        {
            _executor.WakeDelivery();
        }

        for (var write = pass.Ended; write is not null;)
        {
            var next = write.NextEnded;

            write.NextEnded = null;
            Deliver(write);
            write = next;
        }

        if (pass.Scheduled is { } scheduled)
        {
            ThreadPool.UnsafeQueueUserWorkItem(scheduled, preferLocal: false);
        }

        if (pass.Offer is { } offered)
        {
            OfferWaitHold(offered);
        }
    }

    // After the lock: the outcome of a write another thread decided. Its session moves to its position,
    // which cannot fail for a session bound at admission (TryEnter), its call returns, and the write
    // leaves the barrier of the close last. A forwarded write has no call: its refusal is recorded as its
    // reply here, and its success was recorded by the hold that published it (see the class, "Forwarded
    // writes").
    private void Deliver(PendingWrite write)
    {
        write.Registration.Unregister();

        if (write.Forwarded is { } forwarded)
        {
            if (write.Failure is { } refusal)
            {
                forwarded.Refuse(refusal);
            }
        }
        else if (write.Failure is { } failure)
        {
            write.Fail(failure);
        }
        else
        {
            write.Session?.Advance(write.Outcome.Position);
            write.Succeed(write.Outcome);
        }

        _barrier.Exit();
    }

    // The switch of the tests, at the first wait of the resolution of a write, outside every lock.
    private void OfferWaitHold(PendingWrite write)
    {
        if (write.Waited)
        {
            return;
        }

        write.Waited = true;
        RunWaitHold();
    }

    // What one hold of the lock of the writes decided, to be carried out after it (Finish), and the
    // writes it has to start in the hold (Drive). A value on the stack of the hold: nothing is allocated
    // for it.
    private struct Pass
    {
        // The write of the call that holds the lock, if a call does, and whether its outcome was decided.
        public PendingWrite? Own;

        public bool OwnEnded;

        // A write being resolved whose step 3 runs on the thread of its call, which holds the lock.
        public PendingWrite? ReadsHere;

        // A write being resolved whose step 3 runs on a thread of the pool.
        public PendingWrite? Scheduled;

        // A write that begins to wait in step 2 of its resolution, for the switch of the tests.
        public PendingWrite? Offer;

        // Whether a group was published, whether its publication appended to the tail of the children, and
        // whether the database faulted.
        public bool Published;

        public bool Delivered;

        public bool Faulted;

        // The writes to start in this hold, first in first out, linked by Next. A ready write keeps
        // the state it waited in, Chained without the write before it or Held outside the held list,
        // which only Start may see; so Drive starts every ready write before the hold ends, and
        // Withdraw, which takes the lock, never finds one.
        private PendingWrite? _ready;

        private PendingWrite? _readyLast;

        // The writes another thread's call waits for whose outcome this hold decided, linked by NextEnded.
        public PendingWrite? Ended;

        public readonly bool HasReady => _ready is not null;

        public void AddReady(PendingWrite write) => Append(ref _ready, ref _readyLast, write);

        public PendingWrite? TakeReady()
        {
            var write = _ready;

            if (write is not null)
            {
                _ready = write.Next;
                write.Next = null;

                if (_ready is null)
                {
                    _readyLast = null;
                }
            }

            return write;
        }

        public void AddEnded(PendingWrite write)
        {
            write.NextEnded = Ended;
            Ended = write;
        }
    }
}
