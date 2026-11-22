using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// The write coordinator of a master: it decides when each write is admitted, prepared, handed to the
// persistence step and published. Writes are executed one at a time under one lock, the lock of the writes,
// with a group commit at the boundary of the persistence step (docs/design/write-path.md; docs/design/
// milestone-1-engine-contract.md, "Write coordinator and revision semantics"). The path follows
// published designs, taken as ideas and not as code: the single writer of LMDB, which runs every write
// under one writer mutex and checks its conditions against the current state (libraries/liblmdb/mdb.c,
// mdb_txn_begin and mdb_txn_commit), and the group commit of the commit pipeline of Pebble (commit.go:
// the order and the log record under one mutex, publication in order) and of PostgreSQL (xlog.c,
// XLogFlush: whoever finds the log free writes for everyone queued behind it), with the bounds of the
// write group of RocksDB (db/write_thread.cc).
//
// One write, when the persistence step completes at once, as in MemoryOnly. Its call takes the lock once.
// Under it the write is admitted, prepared in place (WriteExecutor.TryPrepare: lookup, condition,
// encoding, reservations), handed to the persistence step as a group of one (IPersistence.CommitAsync,
// called under the lock: it only takes the group), and, since that task is complete, published in the
// same hold. The call releases the lock and returns the result itself: no other thread touches the
// write, and nothing is allocated for it (see "What a write allocates").
//
// When the persistence step is busy. A group whose persistence step did not complete at once is in the
// persistence step until its task completes; the writes prepared meanwhile wait in the queue of the
// prepared writes, and their calls return their tasks. The thread on which the persistence step
// completes takes the lock, publishes the group in its order, gives its writes their outcomes, prepares
// the writes that can be prepared now (below), and hands the next group to the persistence step: at most
// CommitGroup.MaxOperations writes or CommitGroup.MaxEncodedSize bytes from the head of the queue, a
// larger write alone, never waiting to fill. Whoever holds the lock and finds no group in the
// persistence step and prepared writes in the queue hands them over before it releases the lock
// (StartReadyAndCommit), so no prepared write is ever left with nobody to dispatch it: either a group is
// in the persistence step, whose completion will dispatch the queue, or the queue is empty when the lock
// is released. The writes of a group take consecutive positions from CommitOutcome.FirstSequence, in its
// order.
//
// The order of writes to one entity, and the wait behind an unpublished write. A call takes its place in
// the order when it takes the lock: a call made after an earlier call returned its task takes the lock
// after it, and the earlier write was by then published, or prepared and in the queue or in the
// persistence step, or waiting in a chain, in the deferred list or in a resolution. A write whose entity
// has such an earlier write, one that has not completed, waits behind the latest of them, first in first
// out, in the chain of that entity (PendingWrite.EarlierOfEntity, LaterOfEntity), and holds nothing; so
// it is prepared only once every earlier write of its entity has completed, and its condition is checked
// against the state they left. Those writes are found in the list of the writes admitted that waited
// (FindLastSharing, and "The refusal" below); when that list is empty, as in MemoryOnly except while
// the writes a resolution deferred are started, nothing is looked at and no key is hashed for it. The
// wait ends: the write it waits behind completes — published when its persistence step completes (the
// persistence step has no timeout yet), refused, cancelled, failed, or ended by a fault — and every way
// a write completes starts the next write of its chain under the same lock (AdvanceChain). A write with
// a session takes its place when the wait for its position ends. It enters the barrier of the close
// before that wait, so that a close waits for it as for every write admitted; the close ends the wait
// first (EngineDatabase.CloseAsync, PositionClock.Close).
//
// The lock of the writes, given by the database, which takes it as well for its counters and its drain,
// and for the start, the end and the acknowledgments of the children of a master (Children.Capture). It
// protects the arena, the table of root entries, the primary indexes, the position clock, the retired
// list (WriteExecutor) and the tail of the children (Delivery.DeliveryTail), and the state of this
// class: the queue of the prepared writes, the group in the persistence step, the chains, the list of
// the writes admitted that waited, the deferred list, the write being resolved, the admission number,
// whether the database faulted, and the counters. It is held for work in memory only: no I/O, no await,
// no code of the application, no wait for anything but the lock itself. The persistence step is called
// under it and only takes the group (IPersistence). It is the only lock of the writes, and a thread that
// holds it takes no other lock of the database: what a hold decided for other threads — the fault of the
// database, the outcomes of their writes, the end of a resolution — is carried out after the lock is
// released (CarryOutDecisions). A read takes none. A write holds the lock while its values are copied
// into the arena, so one write of a large entity holds every other write back for as long as its copy
// takes; reads are not held back. The lock is not fair: a caller that releases it and takes it again at
// once may pass a caller that waited. What is kept against the unfair hand-over is narrow: a write
// larger than CommitGroup.MaxEncodedSize, the bound above which a write forms a group alone, gives its
// processor up once (Thread.Yield) after its call released the lock, so that a caller the release woke
// can take the lock before the large caller takes it again for its next write. It makes that pass rarer
// and does not bound it; docs/design/write-path.md, "What it costs", gives the measurements, and why a
// gate that hands the lock over first in first out is not used.
//
// Why the hand-overs are ordered (the .NET memory model). Everything a write is and has passes between
// threads only under the lock of the writes, which releases what a thread wrote before it and acquires
// it on the thread that takes it next; the stores of a publication have release semantics
// (Entities.ChangePublisher), so a read that sees them sees the blocks. The outcome of a write that
// another thread completes is written under the lock and given after it, by its value source.
//
// Cancellation (docs/decisions/mutation-commands.md, "Cancellation at the commit boundary"). Until its
// dispatch, the call of IPersistence.CommitAsync for its group, the token of a write is honored at every
// wait — chained, deferred, waiting for another write, prepared and in the queue, waiting in a
// resolution for the writes admitted before it, for the writes in flight or for the reads that hold
// retired memory — and when its
// preparation ends, also a preparation the arena refused. The cancellation of a write that waits in a
// list or in a resolution hands its withdrawal to the thread pool (PendingWrite.Execute, Withdraw), so
// that the thread that cancels never takes the lock of the writes: there it leaves its list, gives back
// what it prepared, starts the next write of its chain, and its call ends in OperationCanceledException.
// The coordinator never makes a cancelled write run before that thread of the pool runs: a prepared
// write whose token is cancelled is withdrawn when a group is formed instead of being taken into it
// (FormGroup), as the work item would withdraw it, and a write that waited in a chain or in the deferred
// list is ended instead of being prepared when its turn comes (Start); either way the next write of its
// entity starts in the same hold, and the work item finds the write ended and does nothing. A write
// whose resolution waits for reads looks at its token in that wait itself. From the dispatch on the
// token is not looked at, and the call returns the real outcome.
//
// The refusal (docs/decisions/write-admission.md). A write is refused for memory only when it does not fit
// beside the stored data, never for memory other writes hold for a time or that is retired and not freed yet.
// A write the arena refuses has given back everything it prepared (WriteExecutor.TryPrepare); it frees what
// is retired and tries again. Refused again, it is resolved alone, holding nothing:
// 1. It waits for every write admitted before it to complete (WaitingForEarlier). The writes that
//    concern it are those of the list of the writes admitted that waited (_oldestWaiter, PendingWrite.Older):
//    a write enters that list when its call leaves the lock with the write not completed, and leaves it
//    when it completes, so the list is in the order of admission, and a write that completed in the
//    hold of its call, which no other write could wait for, never enters it. While it waits in step 1,
//    later writes go on as any write. The end of the last write before it makes it the oldest and goes
//    on to step 2 (MarkCompleted).
// 2. It becomes the write being resolved (_resolving). From then on no write admitted after it begins
//    to be prepared: a later write whose entity has an earlier write that has not completed waits in
//    its chain as any write does, and every other later write waits in the deferred list, first in first
//    out. It waits while any
//    write is prepared and not published (Resolving): in the queue or in the group, all of them
//    later than it, prepared before it became the oldest. Each of them ends: a write that holds the
//    lock hands the queue over, and the persistence step completes. When none is left the wait ends (end of
//    StartReadyAndCommit).
// 3. Then, on a thread of its own outside every lock — the thread of its call when it never had to wait
//    before, otherwise a thread of the pool (EndResolution) — it waits for the reads then in progress
//    that hold retired memory to leave the protection, frees what is retired, and tries once more, with
//    no other write holding prepared memory: every index first gives back its standing reserve, and the
//    write takes what its publication can take, the lesser bound of the reservation, and no top-up.
// 4. If the arena has room it goes on as any write; if not, it is refused with WriteRejected and Limit
//    Memory, has had no effect and never runs later, and every pool is left at the lesser bound of the
//    writes prepared, none: what an index took for it went back to the arena with its withdrawal, not into
//    its reserve. Either way the resolution ends, and the deferred writes start at once, in their order.
// A write that is prepared otherwise tops the standing reserve of each index it changes up, once it has
// all it needs, as far as the arena gives (Entities.ChangePublisher.TopUp); a top-up that cannot be had
// is not a failure. So the reserves count against the memory limit and are never the reason of a refusal
// (docs/design/ordered-index.md, "The standing reserve").
// In MemoryOnly a write completes in the hold of its call, so a refused write is the oldest and nothing
// is prepared and unpublished, and the resolution is the plain one: free what is retired, wait for the
// reads that hold it, retry, refuse. The exception is the end of a resolution: the writes it deferred
// are prepared in one hold and published as one group, and one of them that the arena refuses
// there may find writes before it not completed and writes prepared beside it, and goes through steps
// 1 and 2 as in a database whose persistence step is busy. Only the oldest write is resolved, so two
// writes refused at the same time are resolved one after the other in the order of their admission.
// The wait is finite (exception X6): the writes admitted before it are finite and none of them waits
// for it; the writes it waits for in step 2 are finite, no later one begins, and each ends with its
// persistence step; a read leaves within a step; no write admitted later prolongs it. Its token ends it at
// any step, and the deferred writes then go on. The switch of the tests runs at the first wait of the
// resolution of a write, whichever it is (ArmWaitSwitch).
//
// A value of a unique index (decision on unique indexes). A value, a key with no null part, belongs to one
// entity. The check against the published state is the publisher's, before anything is reserved
// (Entities.ChangePublisher.TryReserve); what the published state does not show, the writes prepared and
// not published, is decided here, in the one try every preparation passes through (TryPrepare), since only
// the coordinator knows them. A value held by a published root of another entity whose prepared write
// keeps it, or that has none, refuses the write with UniqueConstraintViolation and the name of the index,
// of two indexes the one declared first (Violation). A value claimed by a prepared write of another
// entity, an insert or a key move, or held by a published root whose prepared delete or key move frees it,
// makes the write wait for that one write, by the one wait for another write (below): it gives back its
// root, its handle and what it reserved first, and is started again from the beginning once that write
// completes, whatever it completes as, so its check runs again on what that write left. The prepared
// writes are looked at only while prepared memory is held, so in MemoryOnly an ordinary write looks at
// none; each search is linear in them (FindClaimant, FindWriteThatFrees), as the search of the chain of an
// entity is. A write that does not fit the memory
// and would take a value held may end refused for memory, since its root is encoded before the check.
//
// A write batch (docs/decisions/document-model.md, "Atomic WriteBatch within one database"). A batch is one
// write: one record, one admission, one preparation, one place in a group, one position, several changes
// (SubmitBatchAsync). Its members are prepared in their order by the path of a forwarded write, each root
// placed from the bytes the batch of the core encoded when the member was added, and reserved and checked
// together on the state they leave (WriteExecutor.TryPrepareBatch); a failing member refuses the whole
// batch with its place, and a batch the arena cannot hold is resolved and refused as a whole, as any write.
// A group never splits it. The entities of a batch are one set (Entities.PublicationKeys). A batch waits for
// the latest earlier write that has not completed and shares an entity with it, whatever that write is, and
// a single write whose latest earlier write of its entity is a batch waits for that batch, by the one wait
// for another write (below); two single writes keep the chain of their entity. A value of a unique index a
// batch sets or frees is decided as for a single write, over the changes of all its members, and a
// prepared batch answers the question of a later write, who claims and who frees a value, through its
// changes.
//
// One way to wait for another write (PendingWriteStateEnum.WaitingForWrite, PendingWrite.Holder). A write
// that waits for the latest earlier write it shares an entity with, as a batch does, or for the one write
// that holds a value of a unique index it wants, holds nothing, stands in the list of the writes that wait
// for another write, first in first out, and stays the head of the chain of its entity. When that write
// ends, whatever it ends as, its end makes the write ready (MarkCompleted, ReleaseWaitersOf), and the write
// is started again from the beginning (Start), so that it waits again behind whatever then shares an entity
// with it, and its preparation and its checks run again on what that write left. No two writes wait for
// each other: a wait for an entity goes to an earlier admission, and a wait for a value to a prepared write,
// which waits for nothing but its persistence step.
//
// The waits of a write before its dispatch, and how each ends. The wait for the position of its
// session has the timeout of the session and the token, and the close ends it. The wait for the lock of
// the writes is that of a lock: it ends when the holder releases it, after work in memory alone. The
// waits in a chain, in the deferred list, in the queue of the prepared writes and in steps 1 and 2 of a
// resolution have no timeout and have the token; each ends when the writes it waits for end, as above.
// The wait for another write has no timeout and has the token; it ends when that one write completes,
// which waits for nothing that waits for it, as above; a fault ends it (exception X6).
// The wait for the reads that hold retired memory has no timeout and has the token; a read leaves within
// one step (exception X6).
//
// What a write allocates. A write prepared and published in the hold of its call uses the record the
// coordinator keeps for the next write (_spare), which only the holder of the lock takes and gives
// back: no other thread and no registration ever saw it, so it serves again at once, and the call
// returns its result as a value. A write that waits keeps a record of its own, allocated then, with the
// value source of its task, and the collector takes it once nothing holds its task; a token that can be
// cancelled adds its registration. A persistence step that does not complete at once adds the task of its
// completion. A write batch adds, for its call, the array of its members (BatchMember[]), which holds the
// roots encoded when they were added, and the set of its entities (Entities.PublicationKeys), outside the
// lock; for its preparation, once for the batch and used again by each try, the array of its changes
// (PreparedWrite.BatchChanges); and, only when a later write asks it while it is prepared, its sorted claims
// (Entities.UniqueClaims). A record lets go of all of them, and of the values of a single write, at the
// outcome of its write (PendingWrite.LetGoOfInputs): when it serves the next write (ResetAfterItsCall), when
// a resolution on the thread of the call ends the write (Resolve), and before an outcome another thread
// decided is given (GiveOutcome), since the caller may keep the task, whose source is the record, long
// after. So the managed copy of a root lasts from its Add to the outcome of its batch.
//
// Children. A master publishes every write while children are connected or loading; no write is refused
// or delayed because of a child. The capture of a child is registered in a hold of the lock of the
// writes (Children.Capture), and a write published in a later hold is appended to the tail of the
// children in the hold that publishes it (WriteExecutor), so a write is either published before the
// capture starts or delivered after it, whichever hold its preparation was in; the senders of the
// children are woken after the hold (CarryOutDecisions). A child admits no write here: it forwards its
// writes to its master (Child.Forwarding.Forwarder).
//
// Forwarded writes. The receiver of a child's connection submits each write the child forwarded, in the
// order it received them, without awaiting one before it submits the next (Children.ChildConnection,
// Forward), so a forwarded write takes its place among the writes of its entity as any write does. It
// has no token: a request received whole is a submitted write and runs although its child's connection
// ends. It brings the root the child sent instead of values (ForwardedOrigin), which the executor places
// when it prepares the write. It has no caller: its outcome is its reply, recorded for the child before
// the write leaves the barrier of the close, whatever the outcome: a success in the hold that publishes
// it (WriteExecutor), a refusal where its outcome is given, after the hold that decided it (GiveOutcome,
// ReturnOwnOutcome), or at once when it is refused before its admission; why the child takes the reply
// before the transaction, Delivery.RecordedReplies says ("The order that matters"). So once the barrier
// has drained, every forwarded write admitted has its reply recorded, and nothing of one runs any more:
// no task of it is awaited.
//
// A fault. A failure of the persistence step or of a publication faults the database: reads are refused
// from then on, a read that retries on the mark of the publication that failed gives up, and the waits
// for a position end (EngineDatabase.Fault). The writes of the group published before the failure get
// their results; the write whose publication failed and the writes of the group after it get
// IndeterminateOutcome, the first with the failure as its InnerException, and when the persistence step
// itself failed the whole group does; every other write admitted, one that waits for another write among them,
// gets Unavailable once it has released what it held. Every later write is Unavailable, and a close
// works.
internal sealed class WriteCoordinator
{
    // The lock of the writes (see the class).
    private readonly Lock _writes;

    private readonly WriteExecutor _executor;

    // Gives back what a prepared write that is not published took and reserved.
    private readonly ChangePublisher _changes;

    private readonly RetiredMemory _retired;

    private readonly ReadEpochs _epochs;

    // The tail of the transactions for the children, whose senders are woken after a hold that appended.
    private readonly DeliveryTail _delivery;

    private readonly IPersistence _persistence;

    private readonly CloseBarrier _barrier;

    private readonly PositionClock _clock;

    // How long a write waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    // Faults the database (EngineDatabase): its state, its reads and its waits for a position.
    private readonly Action _faultDatabase;

    // The group handed to the persistence step, formed anew for every dispatch.
    private readonly CommitGroup _group = new();

    // The writes prepared and not handed to the persistence step yet, in the order they were prepared.
    private WriteQueue _prepared;

    // Whether the group holds writes, which are prepared and not published: from the first write
    // FormGroup takes into it, through the persistence step, until it is published or the database faults.
    private bool _groupPending;

    // The writes admitted and not completed that waited, the waiters, in the order of their admission
    // (see the class, "The refusal").
    private PendingWrite? _oldestWaiter;

    private PendingWrite? _newestWaiter;

    // The writes deferred while a refused write is resolved, in the order they were deferred.
    private WriteQueue _deferred;

    // The writes that wait for another write, in the order they began to wait (WaitForWrite).
    private WriteQueue _waitingForWrite;

    // The write being resolved after the arena refused it twice, from step 2 of its resolution to its
    // end; null otherwise (see the class).
    private PendingWrite? _resolving;

    // The number of the last write admitted.
    private long _admitted;

    // Set once the persistence step or a publication failed; never cleared.
    private bool _faulted;

    // The counters (EngineCounters), written under the lock and read by the counters of the database.
    private int _queued;

    private int _largestGroup;

    private long _largestGroupBytes;

    // The record for the next write (see the class, "What a write allocates"); null when none is kept.
    private PendingWrite? _spare;

    // The coordinator of a master. A child has one as well, which admits no write: its writes are
    // forwarded to its master (Child.Forwarding.Forwarder).
    public WriteCoordinator(
        Lock writes,
        WriteExecutor executor,
        ChangePublisher changes,
        RetiredMemory retired,
        ReadEpochs epochs,
        DeliveryTail delivery,
        IPersistence persistence,
        CloseBarrier barrier,
        PositionClock clock,
        TimeSpan synchronizationTimeout,
        Action faultDatabase)
    {
        _writes = writes;
        _executor = executor;
        _changes = changes;
        _retired = retired;
        _epochs = epochs;
        _delivery = delivery;
        _persistence = persistence;
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
    // cannot hold; IndeterminateOutcome when a publication failed after the persistence step took it.
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
            return AdmitAfterAsync(waited, table, kind, key, values, members: null, keys: null, expectedRevision, session, cancellationToken);
        }

        return Admit(table, kind, key, values, members: null, keys: null, forwarded: null, expectedRevision, session, cancellationToken);
    }

    // Submits a write batch and returns its task, with its position and whether a member changed the
    // database: its members, in their order (BatchMember), are executed as one write, at one position, or
    // none of them (see the class, "A write batch"). The refusals of SubmitAsync, in the task: for the batch
    // as a whole, WriteRejected with Limit Memory for a batch the memory budget cannot hold; for a member,
    // its refusal with the place of the member in LatticeException.OperationIndex. ArgumentException at the
    // call when two members are of one entity, which the batch of the core never submits.
    public ValueTask<LatticeWriteResult> SubmitBatchAsync(BatchMember[] members, LatticeSession? session, CancellationToken cancellationToken)
    {
        var keys = new PublicationKeys(members.Length);

        foreach (ref readonly var member in members.AsSpan())
        {
            if (!keys.TryAdd(member.Table, member.Key))
            {
                throw new ArgumentException($"The batch has two members for {member.Table.EntityType.Name} '{member.Key}'.", nameof(members));
            }
        }

        foreach (ref readonly var member in members.AsSpan())
        {
            if (member.Table.Layout is null)
            {
                return ValueTask.FromException<LatticeWriteResult>(new NotSupportedException(member.Table.Refusal));
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<LatticeWriteResult>(cancellationToken);
        }

        if (!TryEnter(session, cancellationToken, out var waited, out var refusal))
        {
            return ValueTask.FromException<LatticeWriteResult>(refusal);
        }

        if (!waited.IsCompletedSuccessfully)
        {
            return AdmitAfterAsync(waited, table: null!, kind: default, key: default, values: null, members, keys, expectedRevision: null, session, cancellationToken);
        }

        return Admit(table: null!, kind: default, key: default, values: null, members, keys, forwarded: null, expectedRevision: null, session, cancellationToken);
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

        _ = Admit(table, kind, key, values: null, members: null, keys: null, forwarded, expectedRevision, session, CancellationToken.None);
    }

    // Arms a fault point for the tests of the engine (EngineDatabase.ArmFault).
    public void ArmFault(EngineFaultPointEnum point, int timesToLetThrough)
    {
        lock (_writes)
        {
            _executor.Arm(point, timesToLetThrough);
        }
    }

    // On a thread of the pool, once the token of a write that waits was cancelled (PendingWrite.Execute):
    // a write that waits in a list leaves it, gives back what it prepared, starts the next write of its
    // chain and ends in OperationCanceledException. A write dispatched, one whose resolution waits for
    // reads, which looks at its token itself, and one that has ended are not touched.
    public void Withdraw(PendingWrite write)
    {
        var hold = default(Hold);

        lock (_writes)
        {
            var cancelled = new OperationCanceledException(write.Token);

            switch (write.State)
            {
                case PendingWriteStateEnum.Chained:
                    // Unchain relinks the chain around the write, so its end starts no write (MarkCompleted).
                    Unchain(write);
                    DecideFailure(write, cancelled, ref hold);
                    break;

                case PendingWriteStateEnum.Deferred:
                    _deferred.Remove(write);
                    DecideFailure(write, cancelled, ref hold);
                    break;

                case PendingWriteStateEnum.WaitingForWrite:
                    _waitingForWrite.Remove(write);
                    write.Holder = null;
                    DecideFailure(write, cancelled, ref hold);
                    break;

                case PendingWriteStateEnum.Prepared:
                    _prepared.Remove(write);
                    _changes.Release(write.Prepared!.Changes);
                    DecideFailure(write, cancelled, ref hold);
                    break;

                case PendingWriteStateEnum.WaitingForEarlier:
                    DecideFailure(write, cancelled, ref hold);
                    break;

                case PendingWriteStateEnum.Resolving:
                    _resolving = null;
                    DecideFailure(write, cancelled, ref hold);
                    ReleaseDeferred(ref hold);
                    break;
            }

            StartReadyAndCommit(ref hold);
        }

        CarryOutDecisions(ref hold);
    }

    // Step 3 of the resolution of a refused write (see the class), on a thread of the pool, once the
    // writes it waited for in step 2 were published (PendingWrite.Execute).
    public void EndResolution(PendingWrite write)
    {
        var hold = Resolve(write, own: false);

        CarryOutDecisions(ref hold);
    }

    // A chained write leaves the chain of its entity; the writes behind it wait behind the one before
    // it. A write in the state Chained outside a hold of the lock always has the write before it: only
    // AdvanceChain clears it, and the write it makes ready is started in the same hold
    // (StartReadyAndCommit, Hold), so Withdraw, the only caller, never meets a chained write without
    // one.
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
    // it runs (PositionClock.Bind; docs/decisions/session-consistency.md, "Facade handoff"): a session
    // of another history, and an empty one that another database bound first, is refused before anything
    // is executed. So the session of a write that committed is of this history, and moving it to the
    // position of the write cannot fail: the result of a commit is never replaced by a failure of the
    // session (ReturnOwnOutcome, GiveOutcome).
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
        BatchMember[]? members,
        PublicationKeys? keys,
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

        return await Admit(table, kind, key, values, members, keys, forwarded: null, expectedRevision, session, cancellationToken).ConfigureAwait(false);
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

        _ = Admit(table, kind, key, values: null, members: null, keys: null, forwarded, expectedRevision, session, CancellationToken.None);
    }

    // The call of a write inside the barrier of the close (see the class): one hold of the lock that
    // admits it and, when nothing makes it wait, prepares, dispatches and publishes it; then the outcome,
    // or the task when it waits. The write leaves the barrier when its outcome is given.
    private ValueTask<LatticeWriteResult> Admit(
        EntityTable table,
        WriteKindEnum kind,
        in EntityKey key,
        object?[]? values,
        BatchMember[]? members,
        PublicationKeys? keys,
        ForwardedOrigin? forwarded,
        LatticeRevision? expectedRevision,
        LatticeSession? session,
        CancellationToken cancellationToken)
    {
        var hold = default(Hold);
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
                failure = ReadEpochs.FaultedRefusal();
            }
            else
            {
                write = _spare ?? new PendingWrite(this);
                _spare = null;
                write.Begin(table, kind, key, values, forwarded, expectedRevision, session, cancellationToken, ++_admitted);

                if (members is not null)
                {
                    write.BeginBatch(members, keys!);
                }

                _queued++;
                hold.Own = write;
                Start(write, ref hold);
                StartReadyAndCommit(ref hold);

                if (!hold.OwnEnded)
                {
                    AddWaiter(write);
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

        CarryOutDecisions(ref hold);

        if (write is null)
        {
            return ReturnOwnOutcome(session, forwarded, result, failure);
        }

        // A write refused twice with nothing to wait for is resolved on the thread of its call.
        if (hold.WaitsForReadsHere is not null)
        {
            var resolved = Resolve(write, own: true);

            CarryOutDecisions(ref resolved);

            if (resolved.OwnEnded)
            {
                result = write.Outcome;
                failure = write.Failure;
                hold.OwnEnded = true;
            }
        }

        if (hold.OwnEnded)
        {
            // A write larger than a group gives its processor up once after it released the lock (see
            // the class, "The lock of the writes"), so that a caller woken by that release can take the
            // lock before this caller takes it again.
            if (large)
            {
                Thread.Yield();
            }

            return ReturnOwnOutcome(session, forwarded, result, failure);
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
    private ValueTask<LatticeWriteResult> ReturnOwnOutcome(LatticeSession? session, ForwardedOrigin? forwarded, LatticeWriteResult result, Exception? failure)
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
    // before it in its chain, or at the end of a resolution that deferred it.
    private void Start(PendingWrite write, ref Hold hold)
    {
        if (_faulted)
        {
            DecideFailure(write, ReadEpochs.FaultedRefusal(), ref hold);

            return;
        }

        // A write whose token was cancelled while it waited ends here, before anything is prepared for
        // it, whether or not the thread of the pool its cancellation went to has run.
        if (write.Token.IsCancellationRequested)
        {
            DecideFailure(write, new OperationCanceledException(write.Token), ref hold);

            return;
        }

        if (FindLastSharing(write) is { } last)
        {
            // Two single writes of one entity keep its chain. A write batch, or a single write whose
            // latest earlier write of its entity is a batch, waits for that one write (see the class, "One
            // way to wait for another write").
            // Why the write found has nothing behind it, and no cycle forms: docs/design/write-path.md.
            if (write.Members is not null || last.Members is not null)
            {
                WaitForWrite(write, last);

                return;
            }

            // The latest earlier write of the entity has no write behind it: one admitted after it would
            // be the latest.
            last.LaterOfEntity = write;
            write.EarlierOfEntity = last;
            write.State = PendingWriteStateEnum.Chained;

            return;
        }

        if (_resolving is { } resolving && write.Admission > resolving.Admission)
        {
            write.State = PendingWriteStateEnum.Deferred;
            _deferred.Append(write);

            return;
        }

        Prepare(write, ref hold);
    }

    // Under the lock: the latest write that shares an entity with the write (PendingWrite.SharesEntityWith),
    // was admitted before it, has not completed and waited: prepared and unpublished, in a chain, deferred,
    // waiting for another write, or in a resolution; null when there is none. The write waits for it, so
    // every such wait goes to an earlier admission. Every such write is in the list of the writes
    // admitted that waited (see the class, "The refusal"), and a write of the call that holds the lock
    // is the only one not in it yet, so the list is searched, from the newest write admitted before this
    // one back. Nothing is looked at, and no key is hashed, when the list is empty, as in MemoryOnly
    // outside a resolution. The search is linear in the writes that wait, and so is WriteQueue.Remove in
    // the queue and the deferred list: measured with 40,000 writes waiting behind a busy persistence
    // step, an admission took 0.6 to 0.8 ms. It never runs while the persistence step completes at once;
    // whether a durable persistence step that is busy needs a structure that finds them faster is open
    // until that step is built.
    private PendingWrite? FindLastSharing(PendingWrite write)
    {
        for (var waiter = write.IsWaiter ? write.Older : _newestWaiter; waiter is not null; waiter = waiter.Older)
        {
            if (waiter.SharesEntityWith(write))
            {
                return waiter;
            }
        }

        return null;
    }

    // Under the lock: prepares the write, which has no earlier write of its entity that has not
    // completed, and puts it into the queue of the prepared writes; or ends it, makes it wait for the write
    // that holds a value of a unique index, or begins its resolution (see the class, "The refusal").
    private void Prepare(PendingWrite write, ref Hold hold)
    {
        PreparationEnum preparation;
        PreparedWrite? prepared;

        try
        {
            preparation = TryPrepareTwice(write, ref hold, out prepared);
        }
        catch (Exception exception)
        {
            DecideFailure(write, exception, ref hold);

            return;
        }

        if (preparation == PreparationEnum.RefusedForMemory)
        {
            BeginResolution(write, ref hold);

            return;
        }

        if (preparation == PreparationEnum.DecidedByValue)
        {
            return;
        }

        // Once the write has all it needs, the standing reserves of the indexes it changes are topped up
        // (see the class, "The refusal").
        if (TryQueue(write, prepared!, ref hold))
        {
            _changes.TopUp(prepared!.Changes);
        }
    }

    // Under the lock: a write whose preparation ended goes into the queue of the prepared writes; or,
    // when its token was cancelled meanwhile, it gives back what it prepared and ends cancelled: its
    // token is honored when its preparation ends, before it can be dispatched. False when it ended.
    private bool TryQueue(PendingWrite write, PreparedWrite prepared, ref Hold hold)
    {
        if (write.Token.IsCancellationRequested)
        {
            _changes.Release(prepared.Changes);
            DecideFailure(write, new OperationCanceledException(write.Token), ref hold);

            return false;
        }

        write.Prepared = prepared;
        write.State = PendingWriteStateEnum.Prepared;
        _prepared.Append(write);

        return true;
    }

    // Under the lock: the preparation of a write with the first two tries of a refusal of the arena
    // (see the class), each through TryPrepare, which says what each ends with (PreparationEnum). Its token
    // is honored at the end of each try the arena refused, with OperationCanceledException.
    private PreparationEnum TryPrepareTwice(PendingWrite write, ref Hold hold, out PreparedWrite? prepared)
    {
        var preparation = TryPrepare(write, ref hold, out prepared, out _);

        if (preparation != PreparationEnum.RefusedForMemory)
        {
            return preparation;
        }

        write.Token.ThrowIfCancellationRequested();

        // Refused once: what is retired is freed, and the write tries again.
        _retired.Drain();

        preparation = TryPrepare(write, ref hold, out prepared, out _);

        if (preparation == PreparationEnum.RefusedForMemory)
        {
            write.Token.ThrowIfCancellationRequested();
        }

        return preparation;
    }

    // Under the lock: one try of the preparation of the write (WriteExecutor.TryPrepare), which every
    // preparation passes through, so that a value of a unique index is decided in one place, where the
    // writes prepared and not published are known (see the class, "A value of a unique index"): Prepared,
    // with the prepared write; RefusedForMemory, with the refusal of the arena; DecidedByValue when the write
    // was refused for a value or waits for the write that holds one, a value a published root holds
    // (DecideTakenValue) or one a prepared write claims (WaitsForClaim). Inlined, so that a preparation
    // makes the one call of the executor it made before there were unique indexes: left out of line, this
    // try is a call of its own on every write (JIT listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PreparationEnum TryPrepare(PendingWrite write, ref Hold hold, out PreparedWrite? prepared, out LatticeException? refusal)
    {
        if (write.Members is not null)
        {
            return TryPrepareBatch(write, ref hold, out prepared, out refusal);
        }

        var reservation = _executor.TryPrepare(write, out prepared, out refusal, out var taken);

        if (reservation == ReservationEnum.RefusedForMemory)
        {
            return PreparationEnum.RefusedForMemory;
        }

        if (reservation == ReservationEnum.KeyTaken)
        {
            DecideTakenValue(write, taken, operation: null, ref hold);

            return PreparationEnum.DecidedByValue;
        }

        if (write.Table.HasUniqueIndex && HoldsPreparedMemory() && WaitsForClaim(write, prepared!))
        {
            prepared = null;

            return PreparationEnum.DecidedByValue;
        }

        return PreparationEnum.Prepared;
    }

    // Under the lock: one try of the preparation of a write batch (WriteExecutor.TryPrepareBatch), with the
    // outcomes of TryPrepare for the batch as a whole: a value of a unique index is decided as for a single
    // write, over the changes of all its members (see the class, "A value of a unique index").
    private PreparationEnum TryPrepareBatch(PendingWrite write, ref Hold hold, out PreparedWrite? prepared, out LatticeException? refusal)
    {
        var reservation = _executor.TryPrepareBatch(write, out prepared, out refusal, out var taken, out var failing);

        if (reservation == ReservationEnum.RefusedForMemory)
        {
            return PreparationEnum.RefusedForMemory;
        }

        if (reservation == ReservationEnum.KeyTaken)
        {
            DecideTakenValue(write, taken, failing, ref hold);

            return PreparationEnum.DecidedByValue;
        }

        if (HoldsPreparedMemory() && WaitsForClaim(write, prepared!))
        {
            prepared = null;

            return PreparationEnum.DecidedByValue;
        }

        return PreparationEnum.Prepared;
    }

    // Under the lock, after a preparation that found a value of a unique index taken on the state it leaves
    // (ReservationEnum.KeyTaken, with what it found): when a published root of another entity holds it and
    // that entity has a prepared write that gives the value up, a delete or a key move, the write waits for
    // it; otherwise, also when that write keeps the value, or when two members of a batch claim it, the
    // write is refused (Violation). The operation is the place of the failing member of a batch, null for a
    // single write.
    private void DecideTakenValue(PendingWrite write, in TakenValue taken, int? operation, ref Hold hold)
    {
        if (taken.Owner != default && HoldsPreparedMemory() && FindWriteThatFrees(taken.Index, taken.Owner) is { } holder)
        {
            WaitForWrite(write, holder);

            return;
        }

        DecideFailure(write, Violation(write, taken.Index, operation), ref hold);
    }

    // Under the lock, for a write just prepared and reserved, while writes hold prepared memory: when a
    // prepared and unpublished write of another entity claims a value a change of the write sets or moves,
    // which the published state does not show, the write gives back what it prepared and waits for that
    // write, true; false when none does. A delete sets no value. A single write comes here only for an
    // entity type with a unique index (TryPrepare).
    private bool WaitsForClaim(PendingWrite write, PreparedWrite prepared)
    {
        foreach (ref readonly var change in prepared.Changes)
        {
            if (change.Kind != ChangeKindEnum.Delete && change.Table.HasUniqueIndex && FindClaimant(change) is { } holder)
            {
                _changes.Release(prepared.Changes);
                prepared.Withdraw();
                WaitForWrite(write, holder);

                return true;
            }
        }

        return false;
    }

    // Under the lock, while writes hold prepared memory: the prepared and unpublished write of another
    // entity with a change that claims a value of a unique index the change given sets or moves, which the
    // published state does not show (Entities.ChangePublisher.ClaimsSameKey); null when there is none. A
    // prepared batch answers through its sorted claims, not by a scan of its changes (Entities.UniqueClaims).
    private PendingWrite? FindClaimant(in PreparedChange change)
    {
        foreach (var held in PreparedNow)
        {
            if (held.IsBatch ? held.ClaimsOf(_changes).Claims(change) : held.ChangesEntity && _changes.ClaimsSameKey(change, held.Change))
            {
                return held.Write;
            }
        }

        return null;
    }

    // Under the lock, while writes hold prepared memory: the prepared and unpublished write with a change of
    // the entity of the root entry given, whose published root holds a value of the unique index at the
    // place given, when that change gives the value up (Entities.ChangePublisher.Frees); null when there is
    // none or it keeps the value. Only one write of an entity is prepared at a time, so its write is found by
    // its root entry; a prepared batch finds it among its sorted root entries (Entities.UniqueClaims).
    private PendingWrite? FindWriteThatFrees(int index, RootHandle owner)
    {
        foreach (var held in PreparedNow)
        {
            if (held.IsBatch
                ? held.ClaimsOf(_changes).Frees(owner, index)
                : held.ChangesEntity && held.Change.Handle == owner && _changes.Frees(held.Change, index))
            {
                return held.Write;
            }
        }

        return null;
    }

    // Under the lock: the writes prepared and not published, which the searches for the holder of a value
    // walk (PreparedWrites).
    private PreparedWrites PreparedNow => new(_prepared.First, _groupPending ? _group.Writes : []);

    // Under the lock: the write waits, holding nothing, for the one other write given, in the list of the
    // writes that wait for another write; it stays the head of the chain of its entity, and starts again from
    // the beginning when that write completes (MarkCompleted). The wait ends (see the class, "One way to wait
    // for another write").
    private void WaitForWrite(PendingWrite write, PendingWrite holder)
    {
        write.Holder = holder;
        write.State = PendingWriteStateEnum.WaitingForWrite;
        _waitingForWrite.Append(write);
    }

    // Under the lock, at the end of a write: the writes that wait for it become ready, in the order they began
    // to wait, and are started again from the beginning in this hold (StartReadyAndCommit), so that each
    // check runs again on what the write left. Linear in the writes that wait for another write.
    private void ReleaseWaitersOf(PendingWrite holder, ref Hold hold)
    {
        var waiting = _waitingForWrite;

        _waitingForWrite = default;

        while (waiting.TakeFirst() is { } waiter)
        {
            if (ReferenceEquals(waiter.Holder, holder))
            {
                waiter.Holder = null;
                hold.AddReady(waiter);
            }
            else
            {
                _waitingForWrite.Append(waiter);
            }
        }
    }

    // The refusal of a write that would give a value of a unique index a second owner, made in this one
    // place: UniqueConstraintViolation with the name of the index at the place given (decision on unique
    // indexes); for a write batch, of the entity type of its failing member, whose place, the operation, is
    // LatticeException.OperationIndex (WriteExecutor.TryPrepareBatch). The write changed nothing.
    private static LatticeException Violation(PendingWrite write, int index, int? operation)
    {
        var (table, key) = operation is { } place ? (write.Members![place].Table, write.Members[place].Key) : (write.Table, write.Key);
        var name = table.Indexes[index].Definition.Name;

        return new LatticeException(
            LatticeErrorEnum.UniqueConstraintViolation,
            $"{table.EntityType.Name} '{key}' would take a value of the unique index {name} that another entity holds; nothing was written.")
        {
            IndexName = name,
            OperationIndex = operation,
        };
    }

    // Under the lock: the resolution of a write refused twice begins (see the class): step 1 while a
    // write admitted before it has not completed, step 2 when it is the oldest.
    private void BeginResolution(PendingWrite write, ref Hold hold)
    {
        // The write of the call that holds the lock enters the list of the writes that waited now: every
        // write in it was admitted before it.
        AddWaiter(write);

        if (write.Older is not null)
        {
            write.State = PendingWriteStateEnum.WaitingForEarlier;
            OfferWaitSwitch(write, ref hold);

            return;
        }

        BecomeResolved(write, ref hold);
    }

    // Under the lock: the oldest write, refused twice, becomes the write being resolved (step 2), and
    // goes on to step 3 at once when no write holds prepared memory.
    private void BecomeResolved(PendingWrite write, ref Hold hold)
    {
        _resolving = write;

        if (HoldsPreparedMemory())
        {
            write.State = PendingWriteStateEnum.Resolving;
            OfferWaitSwitch(write, ref hold);

            return;
        }

        BeginWaitForReads(write, ref hold);
    }

    // Under the lock: the switch of the tests is offered to a write that begins to wait in its
    // resolution, after the lock, unless it was offered at an earlier wait.
    private static void OfferWaitSwitch(PendingWrite write, ref Hold hold)
    {
        if (!write.Waited)
        {
            hold.WaitSwitch = write;
        }
    }

    // Under the lock: the write enters the list of the writes admitted that waited, last, unless it is
    // in it.
    private void AddWaiter(PendingWrite write)
    {
        if (write.IsWaiter)
        {
            return;
        }

        write.IsWaiter = true;
        write.Older = _newestWaiter;

        if (_newestWaiter is null)
        {
            _oldestWaiter = write;
        }
        else
        {
            _newestWaiter.Newer = write;
        }

        _newestWaiter = write;
    }

    // Under the lock: the write leaves the list of the writes admitted that waited, when it is in it.
    // Returns the write that became the oldest of the list by it, or null when the write was not the
    // oldest, was not in the list, or was its last.
    private PendingWrite? RemoveWaiter(PendingWrite write)
    {
        if (!write.IsWaiter)
        {
            return null;
        }

        var older = write.Older;
        var newer = write.Newer;

        if (older is null)
        {
            _oldestWaiter = newer;
        }
        else
        {
            older.Newer = newer;
        }

        if (newer is null)
        {
            _newestWaiter = older;
        }
        else
        {
            newer.Older = older;
        }

        write.Older = null;
        write.Newer = null;
        write.IsWaiter = false;

        return older is null ? newer : null;
    }

    // Under the lock: whether a write is prepared and not published, and so holds prepared memory: one
    // is in the queue, or in the group, from the first write the group being formed took (FormGroup)
    // to its publication. Every prepared write that is not published is in one of the two, so the
    // answer is that of the whole state at every moment of a hold, also while a group is formed and a
    // write withdrawn there ends.
    private bool HoldsPreparedMemory() => !_prepared.IsEmpty || _groupPending;

    // Under the lock: the write being resolved goes on to step 3, which waits outside every lock: on the
    // thread of its call when that call holds the lock now, otherwise on a thread of the pool.
    private void BeginWaitForReads(PendingWrite write, ref Hold hold)
    {
        write.State = PendingWriteStateEnum.WaitingForReads;

        if (ReferenceEquals(write, hold.Own))
        {
            hold.WaitsForReadsHere = write;
        }
        else
        {
            write.ResolutionPending = 1;
            hold.Scheduled = write;
        }
    }

    // Step 3 of a resolution and its end (see the class), outside the lock until it takes it for the
    // last try; for the write of the call that runs it when own is true.
    private Hold Resolve(PendingWrite write, bool own)
    {
        var hold = default(Hold);
        long epoch;
        Exception? cancelled = null;

        lock (_writes)
        {
            epoch = _retired.ReadsToWaitFor();
        }

        if (epoch != 0)
        {
            RunWaitSwitch(write);

            try
            {
                // Finite: a read inside the protection waits for nothing and runs no code of the
                // application, so it leaves within one step (exception X6).
                _epochs.WaitForReadsBefore(epoch, write.Token);
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
                hold.Own = write;
            }

            _resolving = null;

            if (_faulted)
            {
                DecideFailure(write, ReadEpochs.FaultedRefusal(), ref hold);
            }
            else if (cancelled is not null)
            {
                DecideFailure(write, cancelled, ref hold);
            }
            else
            {
                TryPrepareResolved(write, ref hold);
            }

            ReleaseDeferred(ref hold);
            StartReadyAndCommit(ref hold);
        }

        // The write of the call, ended in this hold, gives its outcome itself (Admit) and does not serve the
        // next write, and when it was published its group keeps its record until the next group is formed:
        // it lets go of its inputs here, as a write does whose outcome another thread gives (GiveOutcome).
        if (hold.OwnEnded)
        {
            write.LetGoOfInputs();
        }

        return hold;
    }

    // Under the lock: the last try of a resolved write, with no other write holding prepared memory:
    // into the queue of the prepared writes, or refused for memory.
    private void TryPrepareResolved(PendingWrite write, ref Hold hold)
    {
        try
        {
            // The standing reserves of the indexes are never the reason of a refusal: every index gives
            // back what its pool holds above the lesser bound of the writes prepared, none now, and the write
            // takes its own with no top-up (see the class).
            _retired.Drain();
            _changes.GiveBackReserves();

            var preparation = TryPrepare(write, ref hold, out var prepared, out var refusal);

            if (preparation == PreparationEnum.Prepared)
            {
                _ = TryQueue(write, prepared!, ref hold);

                return;
            }

            if (preparation == PreparationEnum.DecidedByValue)
            {
                return;
            }

            // A token cancelled while it was tried ends it as cancelled, not refused.
            write.Token.ThrowIfCancellationRequested();
            DecideFailure(write, refusal!, ref hold);
        }
        catch (Exception exception)
        {
            DecideFailure(write, exception, ref hold);
        }
    }

    // Under the lock: the deferred writes start, in their order, as the end of a resolution lets them.
    private void ReleaseDeferred(ref Hold hold)
    {
        while (_deferred.TakeFirst() is { } deferred)
        {
            hold.AddReady(deferred);
        }
    }

    // Under the lock: as long as a write can go on, it goes on: the writes made ready start, and the
    // queue of the prepared writes is handed to the persistence step whenever no group is in it. The loop
    // is left only when no write is ready (see Hold). At the end a resolved write that no longer has
    // anything to wait for goes on to step 3.
    private void StartReadyAndCommit(ref Hold hold)
    {
        while (true)
        {
            if (hold.TakeReady() is { } ready)
            {
                Start(ready, ref hold);

                continue;
            }

            if (_groupPending || _faulted)
            {
                break;
            }

            if (FormGroup(ref hold))
            {
                Commit(ref hold);

                continue;
            }

            // No group was formed, and a write FormGroup withdrew may have made the next write of its
            // entity ready.
            if (!hold.HasReady)
            {
                break;
            }
        }

        if (_resolving is { State: PendingWriteStateEnum.Resolving } resolving && !HoldsPreparedMemory())
        {
            BeginWaitForReads(resolving, ref hold);
        }
    }

    // Under the lock: the next group, from the head of the queue of the prepared writes; false when no
    // write was taken. A prepared write whose token is cancelled is withdrawn here instead of being
    // taken into the group, whether or not the thread of the pool its cancellation went to has run (see
    // the class, "Cancellation"); the next write of its entity becomes ready, and StartReadyAndCommit
    // starts it. The writes taken are dispatched from here on: the group is handed to the persistence
    // step next, and no token is looked at any more. From the first of them on the group holds prepared
    // memory (_groupPending), so that a write withdrawn after it, whose end may make a refused write the
    // one being resolved, does not find nothing prepared while the group holds it (HoldsPreparedMemory).
    private bool FormGroup(ref Hold hold)
    {
        _group.Clear();

        while (_prepared.First is { } write)
        {
            if (write.Token.IsCancellationRequested)
            {
                _prepared.TakeFirst();
                _changes.Release(write.Prepared!.Changes);
                DecideFailure(write, new OperationCanceledException(write.Token), ref hold);

                continue;
            }

            if (!_group.TryAdd(write.Prepared!))
            {
                break;
            }

            _groupPending = true;
            write.State = PendingWriteStateEnum.Dispatched;
            _prepared.TakeFirst();
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

    // Under the lock: the persistence step for the group formed (the dispatch), which only takes it
    // (IPersistence). A task already completed means the group is committed, and it is published in
    // this hold; otherwise the thread on which it completes publishes it (CompletePersistenceAsync).
    private void Commit(ref Hold hold)
    {
        ValueTask<CommitOutcome> persisting;

        try
        {
            persisting = _persistence.CommitAsync(_group, CancellationToken.None);
        }
        catch (Exception exception)
        {
            FaultGroup(0, 0, exception, ref hold);

            return;
        }

        if (!persisting.IsCompleted)
        {
            _ = CompletePersistenceAsync(persisting.AsTask());

            return;
        }

        CommitOutcome outcome;

        try
        {
            outcome = persisting.Result;
        }
        catch (Exception exception)
        {
            FaultGroup(0, 0, exception, ref hold);

            return;
        }

        PublishGroup(outcome.FirstSequence, ref hold);
    }

    // A persistence step that did not complete at once: its group is published on the thread on which it
    // completes, never on the thread that dispatched it, which holds the lock and goes on.
    private async Task CompletePersistenceAsync(Task<CommitOutcome> persisting)
    {
        CommitOutcome outcome = default;
        Exception? failure = null;

        try
        {
            outcome = await persisting.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var hold = default(Hold);

        lock (_writes)
        {
            if (failure is null)
            {
                PublishGroup(outcome.FirstSequence, ref hold);
            }
            else
            {
                FaultGroup(0, 0, failure, ref hold);
            }

            StartReadyAndCommit(ref hold);
        }

        CarryOutDecisions(ref hold);
    }

    // Under the lock: publishes the group at consecutive sequences from the first, gives its writes
    // their outcomes, and starts the next write of the chain of each. When a publication failed the
    // database faults first, before a write of the group completes, so that the end of one of them
    // begins no resolution while the writes of the group after the failure still hold prepared memory.
    private void PublishGroup(ulong firstSequence, ref Hold hold)
    {
        var published = _executor.Publish(_group, firstSequence, out var failure);
        var writes = _group.Writes;

        hold.Published = true;
        hold.Delivered |= _executor.TakeDelivered();

        if (failure is not null)
        {
            FaultGroup(firstSequence, published, failure, ref hold);
        }

        _groupPending = false;

        for (var index = 0; index < published; index++)
        {
            var prepared = writes[index];

            DecideSuccess(prepared.Write, new LatticeWriteResult(_clock.At(firstSequence + (ulong)index), prepared.ChangesEntity), ref hold);
        }
    }

    // Under the lock: a failure of the persistence step of the group, or of the publication of its write at
    // the index published (see the class, "A fault"): the writes of the group from that index on are
    // IndeterminateOutcome, and every other write admitted ends with Unavailable, once it has released
    // what it held.
    private void FaultGroup(ulong firstSequence, int published, Exception failure, ref Hold hold)
    {
        _faulted = true;
        _groupPending = false;
        hold.Faulted = true;

        var writes = _group.Writes;

        for (var index = published; index < writes.Count; index++)
        {
            var write = writes[index].Write;

            DecideFailure(write, new LatticeException(
                LatticeErrorEnum.IndeterminateOutcome,
                "The persistence step took the write, and the database faulted before it could show it: its publication, or that of a write before it in its group, failed, or the persistence step itself failed.",
                index == published ? failure : null), ref hold);
        }

        while (_prepared.TakeFirst() is { } prepared)
        {
            _changes.Release(prepared.Prepared!.Changes);
            DecideFailure(prepared, ReadEpochs.FaultedRefusal(), ref hold);
        }

        if (_resolving is { State: PendingWriteStateEnum.Resolving } resolving)
        {
            _resolving = null;
            DecideFailure(resolving, ReadEpochs.FaultedRefusal(), ref hold);
        }

        for (var waiter = _oldestWaiter; waiter is not null;)
        {
            var newer = waiter.Newer;

            if (waiter.State == PendingWriteStateEnum.WaitingForEarlier)
            {
                DecideFailure(waiter, ReadEpochs.FaultedRefusal(), ref hold);
            }

            waiter = newer;
        }

        // The deferred writes, the writes that wait for another write and the writes of every chain start, and
        // end with Unavailable (Start).
        ReleaseDeferred(ref hold);

        while (_waitingForWrite.TakeFirst() is { } waiter)
        {
            waiter.Holder = null;
            hold.AddReady(waiter);
        }
    }

    // Under the lock: the next write of the chain of the write, which has ended or is published, starts.
    private static void AdvanceChain(PendingWrite write, ref Hold hold)
    {
        if (write.LaterOfEntity is { } later)
        {
            write.LaterOfEntity = null;
            later.EarlierOfEntity = null;
            hold.AddReady(later);
        }
    }

    // Under the lock: the write succeeded, and its outcome is given after the lock.
    private void DecideSuccess(PendingWrite write, LatticeWriteResult result, ref Hold hold)
    {
        write.Outcome = result;
        write.Failure = null;
        MarkCompleted(write, ref hold);
    }

    // Under the lock: the write ends with the failure, given after the lock.
    private void DecideFailure(PendingWrite write, Exception failure, ref Hold hold)
    {
        write.Failure = failure;
        MarkCompleted(write, ref hold);
    }

    // Under the lock: the write has ended, with the outcome decided, and what waited for it starts: the
    // writes that wait for it, and the next write of its chain. Every end of a write passes here, so a
    // completed write never has a write waiting for it; AdvanceChain does nothing for a write with nothing
    // behind it, and only a test of the empty list is made when no write waits for another write.
    private void MarkCompleted(PendingWrite write, ref Hold hold)
    {
        write.State = PendingWriteStateEnum.Completed;
        _queued--;

        // The end of the oldest write that waited makes the next one the oldest: when it waits in step 1
        // of its resolution, it is resolved now (see the class, "The refusal").
        if (RemoveWaiter(write) is { State: PendingWriteStateEnum.WaitingForEarlier } oldest && !_faulted)
        {
            BecomeResolved(oldest, ref hold);
        }

        if (ReferenceEquals(write, hold.Own))
        {
            hold.OwnEnded = true;
        }
        else
        {
            hold.AddEnded(write);
        }

        if (!_waitingForWrite.IsEmpty)
        {
            ReleaseWaitersOf(write, ref hold);
        }

        AdvanceChain(write, ref hold);
    }

    // After the lock: what the hold decided. The database faults first, so that the reads give up; the
    // waits for a position are woken after a publication; every other write that ended gets its
    // outcome; a resolution that goes on to step 3 goes to the thread pool; the switch of the tests runs
    // for a write that begins to wait in its resolution.
    private void CarryOutDecisions(ref Hold hold)
    {
        if (hold.Faulted)
        {
            _faultDatabase();
        }

        if (hold.Published)
        {
            _clock.Wake();
        }

        if (hold.Delivered)
        {
            _delivery.Wake();
        }

        for (var write = hold.Ended; write is not null;)
        {
            var next = write.NextEnded;

            write.NextEnded = null;
            GiveOutcome(write);
            write = next;
        }

        if (hold.Scheduled is { } scheduled)
        {
            ThreadPool.UnsafeQueueUserWorkItem(scheduled, preferLocal: false);
        }

        if (hold.WaitSwitch is { } offered)
        {
            RunWaitSwitch(offered);
        }
    }

    // After the lock: the outcome of a write another thread decided. Its session moves to its position,
    // which cannot fail for a session bound at admission (TryEnter), its call returns, and the write
    // leaves the barrier of the close last. A forwarded write has no call: its refusal is recorded as its
    // reply here, and its success was recorded by the hold that published it (see the class, "Forwarded
    // writes"). The record is the source of the task of the call, which the caller may keep after its
    // outcome, so it lets go of the inputs of the write first, the roots of a batch among them (see the
    // class, "What a write allocates").
    private void GiveOutcome(PendingWrite write)
    {
        write.Registration.Unregister();
        write.LetGoOfInputs();

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

    // The switch of the tests, at the first wait of the resolution of a write, outside every lock
    // (Protection.RetiredMemory.RunWaitSwitch).
    private void RunWaitSwitch(PendingWrite write)
    {
        if (write.Waited)
        {
            return;
        }

        write.Waited = true;
        _retired.RunWaitSwitch();
    }

    // What one hold of the lock of the writes decided, to be carried out after it (CarryOutDecisions),
    // and the writes it has to start in the hold (StartReadyAndCommit). A value on the stack of the
    // hold: nothing is allocated for it.
    private struct Hold
    {
        // The write of the call that holds the lock, if a call does, and whether its outcome was decided.
        public PendingWrite? Own;

        public bool OwnEnded;

        // A write being resolved whose step 3 runs on the thread of its call, which holds the lock.
        public PendingWrite? WaitsForReadsHere;

        // A write being resolved whose step 3 runs on a thread of the pool.
        public PendingWrite? Scheduled;

        // A write that begins to wait in step 2 of its resolution, for the switch of the tests.
        public PendingWrite? WaitSwitch;

        // Whether a group was published, whether its publication appended to the tail of the children, and
        // whether the database faulted.
        public bool Published;

        public bool Delivered;

        public bool Faulted;

        // The writes to start in this hold, first in first out. A ready write keeps the state it waited
        // in, Chained without the write before it, Deferred outside the deferred list or WaitingForWrite
        // outside the list of the writes that wait for another write, which only Start may see; so
        // StartReadyAndCommit starts every ready write before the hold ends, and Withdraw, which takes the
        // lock, never finds one.
        private WriteQueue _ready;

        // The writes another thread's call waits for whose outcome this hold decided, linked by NextEnded.
        public PendingWrite? Ended;

        public readonly bool HasReady => !_ready.IsEmpty;

        public void AddReady(PendingWrite write) => _ready.Append(write);

        public PendingWrite? TakeReady() => _ready.TakeFirst();

        public void AddEnded(PendingWrite write)
        {
            write.NextEnded = Ended;
            Ended = write;
        }
    }

    // The writes prepared and not published, those of the queue of the prepared writes in its order and
    // then those of the group in the persistence step while it is pending, for a foreach: what the searches
    // for the holder of a value walk (FindClaimant, FindWriteThatFrees). A value on the stack of a search,
    // which allocates nothing; linear in the writes it walks.
    private struct PreparedWrites
    {
        private readonly IReadOnlyList<PreparedWrite> _group;

        private PendingWrite? _next;

        private int _inGroup;

        public PreparedWrites(PendingWrite? first, IReadOnlyList<PreparedWrite> group)
        {
            _next = first;
            _group = group;
            _inGroup = -1;
            Current = null!;
        }

        public PreparedWrite Current { get; private set; }

        public readonly PreparedWrites GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_next is { } queued)
            {
                Current = queued.Prepared!;
                _next = queued.Next;

                return true;
            }

            if (++_inGroup < _group.Count)
            {
                Current = _group[_inGroup];

                return true;
            }

            return false;
        }
    }

    // A queue of writes, first in first out, linked through PendingWrite.Next: the queue of the prepared
    // writes, the deferred list, the writes that wait for another write, and the writes a hold has to start
    // (Hold). A write is in one of them at
    // most. A value in a field of its owner, changed in place, under the lock of the writes. The list of
    // the writes that waited, whose writes leave it from any place, and the writes a hold ended, which
    // are only pushed and walked once, have links of their own (PendingWrite.Older, Newer, NextEnded).
    private struct WriteQueue
    {
        private PendingWrite? _first;

        private PendingWrite? _last;

        public readonly bool IsEmpty => _first is null;

        // The first write, which stays in the queue; null when it is empty.
        public readonly PendingWrite? First => _first;

        public void Append(PendingWrite write)
        {
            write.Next = null;

            if (_last is null)
            {
                _first = write;
            }
            else
            {
                _last.Next = write;
            }

            _last = write;
        }

        // Takes the first write out; null when the queue is empty.
        public PendingWrite? TakeFirst()
        {
            var write = _first;

            if (write is not null)
            {
                _first = write.Next;
                write.Next = null;

                if (_first is null)
                {
                    _last = null;
                }
            }

            return write;
        }

        // Takes the write out, which the queue holds; linear in the queue (see FindLastSharing).
        public void Remove(PendingWrite write)
        {
            PendingWrite? before = null;

            for (var current = _first; current is not null; current = current.Next)
            {
                if (!ReferenceEquals(current, write))
                {
                    before = current;

                    continue;
                }

                if (before is null)
                {
                    _first = write.Next;
                }
                else
                {
                    before.Next = write.Next;
                }

                if (ReferenceEquals(_last, write))
                {
                    _last = before;
                }

                write.Next = null;

                return;
            }
        }
    }
}
