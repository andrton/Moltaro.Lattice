using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Engine.Children;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Following;
using Moltaro.Lattice.Engine.Forwarding;
using Moltaro.Lattice.Engine.Loading;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Sets;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine;

// The database of the engine: an ILatticeDatabase over the native arena, the table of root
// entries and the root layouts of this assembly, with the primary index. It is what the public
// contract runs on: LatticeDatabase opens it (brief of Epic 1, sprint S12). It is a MemoryOnly master that
// inserts, reads, replaces, upserts and deletes entities of scalar fields by primary key, by class
// and by name (brief of Epic 1, sprint S06), for many callers at once through its write coordinator
// (S09), and that accepts children at a ListenUri; or a child that loads the whole database of its
// master over TCP, serves its reads (S10), follows the writes of its master (S11), and forwards its own
// writes to its master, each returning once this child has applied it (S12).
//
// How it is made. LatticeDatabase.Create makes it, and the tests of the engine and the measurement program
// make it with their inputs through Create (exception X3, docs/work/exceptions.md); the scenario tests of
// the contract open it through LatticeDatabase, and every operation after it goes through the public
// interfaces. An instance is a MemoryOnly master, with or without a ListenUri, or a child. It is made
// without being opened, and the refusals come in the order of LatticeDatabase.Create: ArgumentNullException
// for null options; Validate, which gives InvalidConfiguration, also for an address that does not have the
// form of the transport; then NotSupportedException for Durable persistence (until S27). A master that
// cannot listen at its address fails its open with InvalidConfiguration.
//
// States. A master is Created, Loading during OpenAsync, Ready, Faulted, Closed. A child is Connecting
// while it has no connection to its master, the pauses between its attempts included, and Loading from
// the accepted handshake until its load is complete, the transactions up to its readiness position
// applied; in both every operation is Unavailable (the coordinator's reading of 17 November 2026).
// CatchingUp does not occur: catching up to R is part of Loading. OpenAsync compiles the root
// layout of every entity type before any data is admitted: a fixed
// part larger than one block fails the open with InvalidSchema, and an entity type with a field the
// engine cannot store yet gets no layout (the planner's answer to point 1 of Q35). A failed open
// closes the instance. Before the open an operation is Unavailable; after a close it is
// ObjectDisposedException. A failed publication makes the instance Faulted (Fault; brief of S09,
// "Faulted"): from then on every operation, reads included, is Unavailable, a read that retries on
// the mark of the write that failed gives up, the writes in progress end as the write coordinator
// says, and a close works. A MemoryOnly instance that faulted has lost its data.
//
// The close. It lets every write admitted before it reach its outcome and frees the arena once
// nothing can reach it any more: it marks the reads closed, so that a read that enters afterwards
// leaves at once (ReadEpochs), lets no write, drain or count of the counters start (CloseBarrier),
// waits for those in progress, every admitted write in whatever step it is among them, and then for
// the reads that entered before the mark, and only then frees the arena with everything retired. It
// does not wait for that inside the call: CloseAsync returns a task that completes then, and a
// cancellation of its token ends only the wait of the caller. A wait for a position ends with the
// close. The close is one operation (brief of S09, part 2, the first finding of the outside review it
// carries): the first call of CloseAsync or DisposeAsync begins it and keeps its task, and every
// later call waits for that same task, so no call completes before the arena is freed, whichever
// call it is and whatever became of the wait of the first; the synchronous Dispose blocks its thread
// until then. The close covers the network (the planner's block of 17 November 2026, point 7, and his
// decision after the study of S12, point 4, and at the review of S12, point 2): on a master, once its
// admitted writes have ended, each with its reply recorded when a child forwarded it, it stops accepting
// children, has each connection send its child what it owes it, the replies and then the tail, then the
// frame Closed, and end its side in order, waits for the children to end theirs, all together and at most
// a fixed time, resets those still open, and completes only when no task of the listener runs and its
// port is free (Children.ChildListener); on a child it ends the open in progress and the following of its
// master, waits for their tasks, and closes the connection to the master, without waiting for the master:
// its writes in flight end as the forwarding says (Forwarding). A suspension of the application held at the close does not keep it from
// completing; a master whose child holds one waits out the fixed time for that child.
//
// Master and children (briefs of S10 and S11). A master with a ListenUri listens at it from its open and
// serves every node that connects (Children): it checks its Hello, registers its capture under the lock
// of the writes — its start S, its fence and its cursor in the tail of the transactions (Delivery) —,
// sends the whole database, one root copied at a time under the protection of reads while it goes on
// writing, then LoadEnd with S and R, and then every transaction after S, as long as the child is
// connected; it releases a transaction once every child has applied it. No write is refused or delayed
// because of a child. A child reaches its master at MasterUri (Loading.MasterLink), receives the schema,
// builds the parts of an empty database with the history of its master, places every root it receives
// under its own lock of the writes (Loading.RootPlacement), applies the transactions of (S, R] by the
// same rule (Loading.ReceivedChanges, Following.Follower), and becomes Ready at R. It then follows its
// master, and tells it what it applied, until the connection ends or it cannot apply a transaction; then
// it stops following and stays Ready at its position (reconnection is S34). It forwards its writes to its
// master on the same connection, and the master submits them to its write path as its own (S12;
// Forwarding.Forwarder, Children.RequestReceiver); a write through a child returns once that child has
// applied it, and a child without its master refuses a write with Unavailable. What travels, and how it
// is checked, is the protocol of Replication. The open of a child that
// fails, times out or is cancelled closes the instance and completes only once the close has: no
// connection, no task and no memory of it is left.
//
// What the parts do, and where. The entity types and their reads are in Entities (EntityTable, one
// for each entity type, with its root layout), the text entries of Dictionary strings in the arena
// (Storage.Text.TextTable, which only writes use), the primary indexes in Indexes (PrimaryIndex), the
// writes in Writes (WriteCoordinator, which runs them under the lock of the writes and groups them
// for the storage step, and WriteExecutor, which prepares and publishes them), the protection of reads by epochs, what is retired and the barrier of the close in
// Protection, the history and the sequence in Positions (PositionClock), the sets of the public
// interfaces in Sets, what a master gives its children in Children, the tail of the transactions it
// holds for them in Delivery, how a child loads in Loading, how it follows its master in Following, and
// how it forwards its writes in Forwarding.
//
// The order of the locks and of the lifetimes. The database has two locks: the lock of the state,
// for an open, a close and a fault that race; and the lock of the writes (WriteCoordinator, which says
// what it protects), under which writes are admitted, prepared, handed to the storage step and
// published, under which a drain or a count of the counters runs, under which the capture of a child
// starts and ends and its acknowledgments release the tail on a master, and under which a root is
// placed and a transaction applied on a child, held for synchronous work in memory only. The lock of
// the state is taken alone; no thread that holds a lock takes the other one. The listener of a master
// has a lock of its own for its connections; the follower of a child has one for its suspensions, its
// sender one for its queue and the table of its pending requests one; each is taken alone as well. No
// lock is held while bytes are sent or received.
// A read takes none: it enters the protection of reads, which is no lock and never waits for the writer, and
// nothing is taken inside it. What a read can reach lives at least as long as the read: a write
// unlinks it and retires it, and the writer frees it only once the reads that entered before have
// left (RetiredMemory); the close frees the arena only after the reads and the writes in progress
// have ended. No code of the application runs, and nothing waits, while a read is inside.
//
// Position and revision. Each open of a master has a new random non-zero history, and a new random
// database identity, which it sends its children; a child has the history of its master, and the
// revision of a loaded entity is its revision on the master. On a master the sequence is 0 and grows
// by 1 with each published write, also one that changed nothing (the owner's answer to Q39); the
// storage step assigns it, consecutive within a group (WriteCoordinator). A revision is the history
// and the root entry's LastMutationSequence: every position changes at most one entity, so the
// revision of one entity never satisfies a condition on another, and a recreated entity has a new
// one. LatticeRevision is two opaque 64-bit values, so this composition is internal and can change;
// how a revision binds its entity when several entities change at one position belongs to the stage
// with the batch (milestone contract, "Write coordinator and revision semantics"; D4 of Epic 1).
//
// The memory budget. LatticeDatabaseOptions.MemoryLimit is the limit of the arena, the native memory
// in which the entities are stored, with the blocks that writes in progress prepared there and what
// writes retired until the reads that could reach it have left; without it, half of
// GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, read at open, and never less than one segment
// (brief of S09, "The refusal" and the answers of the planner, "The computed default"). The limit in
// force is fixed at open; EngineCounters.MemoryLimit reports it. A write the arena cannot hold is
// WriteRejected with Limit Memory, as the write coordinator decides. The indexes, the root entries
// and what an enumeration remembers are not counted until S24. An entity has no size limit of its own
// (decision O19 of Epic 1); one String or Binary value above 268,173,376 bytes, the capacity of one
// directory block of chunks, is ValidationFailed with the field named, a fixed rule of the storage
// format, which the validation of the core checks before a write reaches the engine.
//
// Sessions and the write batch. On a master the members that take a session, and
// WaitForPositionAsync, are served: a master has applied every position it gave out; a child has
// applied every position of its history up to the one it is at, and a wait for a later one ends when it
// applies it or at its timeout, and at once on a child that has lost its master (Positions.PositionClock).
// CreateWriteBatch returns the batch of the core: building it works, and its commit is
// NotSupportedException until S17.
internal sealed class EngineDatabase : ILatticeDatabase
{
    private readonly LatticeDatabaseOptions _options;

    // Guards the changes of the state: an open and a close that race.
    private readonly Lock _stateLock = new();

    private readonly CloseBarrier _barrier;

    private readonly ReadEpochs _epochs;

    // What a test of the engine gave besides the options (the second Create); null for every other
    // instance, which uses what each null member of EngineTestInputs stands for.
    private readonly EngineTestInputs? _testInputs;

    // The sets of classes, made once for each class and shared.
    private readonly ConcurrentDictionary<Type, object> _classSets = new();

    private volatile LatticeDatabaseStateEnum _state = LatticeDatabaseStateEnum.Created;

    // The close, once a call has begun it: completes when the arena has been freed. Set under the lock
    // of the state by the call that sets Closed, and never changed afterwards.
    private TaskCompletionSource? _closed;

    // The parts of the open database. Set by the open before the state becomes Ready, which a member
    // reads first, and never changed afterwards.
    private LatticeSchema? _schema;

    private Arena? _arena;

    private RootTable? _roots;

    private RetiredMemory? _retired;

    private PositionClock? _clock;

    // The lock of the writes, which the counters and the drain take as well (WriteExecutor).
    private Lock? _writes;

    private WriteCoordinator? _coordinator;

    // What the coordinator executes writes with, and the loader of a child places roots with.
    private WriteExecutor? _executor;

    // The tail of the transactions a master holds for its children; on a child it never retains.
    private DeliveryTail? _delivery;

    // On a child: where its writes go, to its master (Forwarding.Forwarder); null on a master.
    private Forwarder? _forwarder;

    private Dictionary<LatticeEntityType, SetOperations>? _operations;

    private Dictionary<LatticeEntityType, RecordSet>? _recordSets;

    // The network of the instance, set under the lock of the state and never changed afterwards: the
    // listener of a master with a ListenUri, set by the open while it is inside the barrier of the close,
    // so that a close that begins meanwhile finds it once the open has left the barrier; and on a child,
    // the connection to its master, set when the master accepted the child, and the end of the task of
    // its open, made when the open begins.
    private ChildListener? _listener;

    private MasterLink? _master;

    private TaskCompletionSource? _loaded;

    // On a child that is Ready: how it follows its master, and the task that does, which the close waits
    // for. Set under the lock of the state before the state becomes Ready, and never changed afterwards.
    private Follower? _follower;

    private Task? _following;

    // Cancelled when the close begins: it ends the open of a child that is in progress.
    private readonly CancellationTokenSource _closing = new();

    // An instance for the options, which Check has accepted, without opening it: LatticeDatabase.Create, and
    // the Create of the tests with their inputs.
    internal EngineDatabase(LatticeDatabaseOptions options, EngineTestInputs? testInputs)
    {
        _options = options;
        _testInputs = testInputs;
        _barrier = new CloseBarrier(this);
        _epochs = new ReadEpochs(this);
    }

    // For the tests of the engine and the measurement program (exception X3, docs/work/exceptions.md):
    // makes an instance as LatticeDatabase.Create does, with the same refusals, that takes what the inputs
    // give instead of what an instance makes for itself: the options of its arena, the allocator of its
    // native memory, the seed of the hashes of its primary indexes and the storage step of its commit
    // stage, each when it is set (EngineTestInputs). When the inputs give the options of the arena,
    // MemoryLimit of the options of the database is not read. Nothing else differs, and an instance made
    // by LatticeDatabase.Create pays nothing for it.
    public static EngineDatabase Create(LatticeDatabaseOptions options, EngineTestInputs inputs)
    {
        Check(options);
        ArgumentNullException.ThrowIfNull(inputs);

        return new EngineDatabase(options, inputs);
    }

    // The refusals of LatticeDatabase.Create, in their order, and of the Create of the tests.
    internal static void Check(LatticeDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.PersistenceMode == LatticePersistenceModeEnum.Durable)
        {
            throw new NotSupportedException("Durable persistence is not implemented yet in the engine. Select MemoryOnly explicitly.");
        }
    }

    // What the engine holds at this moment, for the tests and later for diagnostics: taken between
    // two changes of the engine, under the lock of the writes, so that no change, no publication and no
    // freeing of what was retired is half done; the counters of the coordinator are written under the
    // same lock.
    public EngineCounters Counters
    {
        get
        {
            ThrowIfNotReady();
            _barrier.Enter();

            try
            {
                lock (_writes!)
                {
                    var indexBytes = 0L;
                    var replacementBytes = 0L;

                    foreach (var operations in _operations!.Values)
                    {
                        indexBytes += operations.Table.IndexBytes;
                        replacementBytes += operations.Table.ReplacementIndexBytes;
                    }

                    return new EngineCounters(
                        _arena!.Counters,
                        indexBytes,
                        _retired!.IndexBytes,
                        replacementBytes,
                        _roots!.Count,
                        (long)_clock!.Sequence,
                        _coordinator!.QueuedWrites,
                        _coordinator.LargestCommitGroup,
                        _coordinator.LargestCommitGroupBytes,
                        _arena.MemoryLimit,
                        _delivery!.ActiveCaptures,
                        _delivery.PendingBytes,
                        _arena.Texts.Count,
                        _arena.Texts.Bytes);
                }
            }
            finally
            {
                _barrier.Exit();
            }
        }
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): every entry of the text table
    // with its exact stored bytes, in the encoding of the database, and its count of references (brief of
    // Epic 1, sprint S13, the planner's decision after the study, points 4 and 9), taken as Counters is,
    // under the lock of the writes between two changes of the engine, on a master and on a child. It does
    // not drain: what a root a write replaced or deleted holds until a drain frees it is counted.
    // Unavailable before the open and once Faulted, ObjectDisposedException after the close.
    public List<(byte[] Text, long References)> TextReferences()
    {
        ThrowIfNotReady();
        _barrier.Enter();

        try
        {
            lock (_writes!)
            {
                return _arena!.Texts.References();
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // Frees every retired thing no read in progress can reach — an old root with its blocks, a
    // replaced table of the primary index, the entry of a deleted root — and returns. It does not
    // wait and takes no timeout: with no read in progress it leaves nothing retired, and what a read
    // in progress can still reach stays retired until a later drain (brief of S08, "Counters and the
    // drain, part 1"). It is what a test calls before it counts; a write under memory pressure and a
    // close drain in the same way (WriteExecutor.DrainRetired, FreeAfterOperationsAsync). Unavailable
    // before the open and once Faulted, ObjectDisposedException after the close. It runs under the lock
    // of the writes, between two changes of the engine, because only that thread frees (RetiredMemory).
    public void DrainRetired()
    {
        ThrowIfNotReady();
        _barrier.Enter();

        try
        {
            lock (_writes!)
            {
                _retired!.Drain();
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next read by key that
    // finds its root in the index runs hold, once, on its own thread and inside the protection of
    // reads, after it has found the root and before it copies it, with the handle of the root entry
    // it found. A test waits inside hold, with a bounded wait, to keep that read inside the
    // protection while the test writes, drains, counts and closes; when hold returns, the read goes
    // on as it would have. Every other read passes the point without running anything. The switch
    // can be armed again while a read it held earlier is still held, so that two reads are held at
    // once. A read when no test has armed the switch pays nothing for it beyond reading one field
    // that is null (ReadEpochs.OfferHold). Unavailable before the open, ObjectDisposedException after
    // the close.
    public void HoldNextRead(Action<RootHandle> hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ThrowIfNotReady();

        _epochs.HoldNext(hold);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next growth of the
    // primary index of the entity type runs hold, once, on the thread of the write that grows it and
    // inside the lock of the writes, after it has published the grown table and before it retires the
    // old one, while a step that entered before may still walk the old table and a step that enters
    // now walks the new one. A test waits inside hold, with a bounded wait, to keep the growth there
    // while it takes steps of an enumeration, or counts the growths; it neither writes nor counts the
    // counters meanwhile, since the lock of the writes is held. When hold returns the growth goes on
    // as it would have. A growth when no test has armed the switch pays one load of a field that is
    // null, and a read pays nothing. The entity type has to be one the engine stores. Unavailable
    // before the open, ObjectDisposedException after the close.
    public void HoldNextGrowth(LatticeEntityType entityType, Action hold)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(hold);
        ThrowIfNotReady();

        _operations![entityType].Table.HoldNextGrowth(hold);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next write that the arena
    // refused twice runs hold, once, at the first wait of its resolution, outside every lock, when it
    // knows it has to wait and before it waits, so that a test can tell that the write waits instead of
    // giving it a window of time. A refused write is resolved alone: while it holds back the preparation
    // of later writes it waits for the writes prepared and not published yet, then for the reads that
    // hold retired memory (WriteCoordinator, "The refusal"); the switch runs at the first of these waits
    // that the write has to make. When hold returns the write waits as
    // it would have. Only a write the arena refused loads the switch: no other write and no read pays
    // anything for it. Unavailable before the open, ObjectDisposedException after the close.
    public void HoldNextRefusedWriteWait(Action hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ThrowIfNotReady();

        _coordinator!.HoldNextWait(hold);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next pass of a write
    // through the point throws InvalidOperationException with a message that names the point; tests
    // only. Both points lie after the storage step, and a failure at either puts the instance into
    // Faulted (EngineFaultPointEnum): a write of the group published before the fault gets its result;
    // the write whose publication faulted and the writes of its group
    // after it get IndeterminateOutcome, the first of them with the injected exception as its
    // InnerException; writes not yet dispatched get Unavailable; every later operation, reads
    // included, is Unavailable, and a close works (brief of Epic 1, sprint S09, "Faulted" and the
    // answers of the planner to the contract author). A point does nothing unless a test arms it: a
    // pass loads one field, under the lock of the writes it holds (WriteExecutor). Unavailable before
    // the open and once Faulted, ObjectDisposedException after the close.
    public void ArmFault(EngineFaultPointEnum point) => ArmFault(point, 0);

    // For the tests of the engine, as ArmFault(point): the given number of passes of writes go through
    // the point, and the pass after them throws, so that a test can fault the second write of a group
    // and state what the first, published before the fault, returns (the answers of the planner to the
    // contract author, "After a fault"). ArmFault(point) is ArmFault(point, 0). An armed point costs a
    // pass what ArmFault(point) costs it, and a point nobody armed nothing more. A negative number is
    // ArgumentOutOfRangeException.
    //
    // Added by the test author of S09, because no member of the contract lets a write of a group pass
    // the point before another faults there.
    public void ArmFault(EngineFaultPointEnum point, int skippedPasses)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skippedPasses);
        ThrowIfNotReady();

        _coordinator!.ArmFault(point, skippedPasses);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md; brief of Epic 1, sprint S11,
    // "Types and members of S11"): the internal switch that holds back a child's replication, so that a
    // test can state what a child does while it has not applied what it received. A Ready child holds
    // back the application of every transaction it receives until the suspension is disposed; holds of
    // several suspensions add up. The transaction it has put together waits where it was received, and the
    // child receives nothing more meanwhile, so what its master sends waits in the connection; its master
    // goes on writing.
    // Disposing it after the close does nothing; a suspension held at the close does not keep the close
    // from completing. Unavailable before the child is Ready, ObjectDisposedException after the close,
    // and InvalidOperationException on a master, which applies nothing it receives.
    public IDisposable SuspendApplying()
    {
        ThrowIfNotReady();

        return _follower?.Suspend() ?? throw new InvalidOperationException("A master applies nothing it receives; only a child holds back its application.");
    }

    // For the measurement program (brief of Epic 1, sprint S11, the planner's decision after the study,
    // point 12): what the catch-up of a child applied from LoadEnd to Ready; null on a master and on a
    // child that is not Ready yet. Set before the state becomes Ready and never changed afterwards.
    internal CatchUpReport? CatchUp { get; private set; }

    public LatticeDatabaseStateEnum State => _state;

    public LatticeSchema Schema
    {
        get
        {
            ThrowIfNotReady();

            return _schema!;
        }
    }

    public LatticePosition Position
    {
        get
        {
            ThrowIfNotReady();

            return _clock!.Position;
        }
    }

    // The address the master listens at, with the port the system chose for port 0; null for a master
    // without ListenUri and for a child.
    public Uri? ListenUri
    {
        get
        {
            ThrowIfNotReady();

            return _listener?.Address;
        }
    }

    public ValueTask WaitForPositionAsync(LatticePosition position, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!position.IsValid)
        {
            throw new ArgumentException("The default value is not a position.", nameof(position));
        }

        if (timeout is { } requested && !LatticeDatabaseOptions.IsFiniteTimeout(requested))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "The timeout must be finite and positive.");
        }

        ThrowIfNotReady();

        return _clock!.WaitAsync(position, timeout ?? _options.SynchronizationTimeout, cancellationToken);
    }

    public ValueTask WaitForPositionAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ThrowIfNotReady();

        return _clock!.WaitAsync(session, _options.SynchronizationTimeout, cancellationToken);
    }

    // Opens the database. A master compiles the root layout of every entity type, makes the parts of an
    // empty database, listens at its ListenUri when it has one, and becomes Ready: its work is
    // synchronous and short, so it completes before it returns. A child loads the database of its
    // master (OpenChildAsync). A failure, and a cancellation, close the instance, and the call completes
    // once the close has.
    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        var child = _options.Role == LatticeRoleEnum.Child;

        lock (_stateLock)
        {
            if (_state == LatticeDatabaseStateEnum.Closed)
            {
                return ValueTask.FromException(new ObjectDisposedException(GetType().FullName));
            }

            if (_state != LatticeDatabaseStateEnum.Created)
            {
                return ValueTask.FromException(new InvalidOperationException("The database has already been opened."));
            }

            _state = child ? LatticeDatabaseStateEnum.Connecting : LatticeDatabaseStateEnum.Loading;

            if (child)
            {
                _loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        if (child)
        {
            return new ValueTask(OpenChildAsync(cancellationToken));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenMaster();
        }
        catch (Exception exception)
        {
            var closing = CloseAsync(CancellationToken.None);

            return closing.IsCompleted ? ValueTask.FromException(exception) : new ValueTask(FailAfterAsync(closing, exception));
        }

        return ValueTask.CompletedTask;
    }

    public ILatticeSet<TEntity> Set<TEntity>()
        where TEntity : class
    {
        ThrowIfNotReady();

        // A class that does not bind throws from the factory, and nothing is kept for it.
        return (ILatticeSet<TEntity>)_classSets.GetOrAdd(typeof(TEntity), static (_, database) => database.ClassSetOf<TEntity>(), this);
    }

    public ILatticeSet Set(string entityName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityName);
        ThrowIfNotReady();

        return _schema!.TryGetEntityType(entityName, out var entityType)
            ? _recordSets![entityType]
            : throw new LatticeException(LatticeErrorEnum.BindingFailed, $"The schema has no entity type '{entityName}'.");
    }

    public ILatticeWriteBatch CreateWriteBatch()
    {
        ThrowIfNotReady();

        return new WriteBatch(_schema!);
    }

    // Lets no operation start any more, ends the waits for a position, and frees the arena once the
    // reads and writes in progress have ended (see the class). The first call begins the close; every
    // later call, of this or of DisposeAsync, joins it. The cancellation token ends only the wait of
    // its call: the database still closes, and the arena is freed when they have ended.
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource closed;
        var begins = false;

        lock (_stateLock)
        {
            if (_closed is null)
            {
                _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _state = LatticeDatabaseStateEnum.Closed;
                begins = true;
            }

            closed = _closed;
        }

        if (begins)
        {
            _clock?.Close();

            // The mark first: a read that enters from now on leaves at once, and the reads that entered
            // before announce an epoch below the one returned here.
            var mark = _epochs.Close();

            // The open of a child in progress ends at its next wait, or at its next placement, which the
            // barrier refuses from now on.
            _closing.Cancel();

            _ = FreeAfterOperationsAsync(_barrier.Close(), mark, closed);
        }

        var task = closed.Task;

        return task.IsCompleted ? new ValueTask(task) : new ValueTask(task.WaitAsync(cancellationToken));
    }

    public ValueTask DisposeAsync() => CloseAsync();

    public void Dispose() => CloseAsync().AsTask().GetAwaiter().GetResult();

    // Frees the arena, and with it every block, the retired ones included, once the writes, drains and
    // counts of the counters in progress have ended, the network of the instance has stopped, and then
    // the reads that entered before the mark of the close, and then completes the close. When nothing is
    // in progress it does so before it returns. The wait for the reads runs on a thread of the pool,
    // because a read may be held by a test while its caller goes on; it ends as
    // ReadEpochs.WaitForReadsBefore says.
    //
    // The network, after the operations: an open of a master is one of them while it starts its
    // listener, so the listener is set once they have ended. The listener stops and its tasks end
    // (ChildListener.StopAsync): each connection is reset, and its task leaves the protection of reads
    // at once, since a copy of a root waits for nothing. The open of a child ended its waits when the
    // close began, and its task ends at once; then its connection to the master closes.
    //
    // Every step runs whatever became of the one before (review round 1 of S10): a task of the network
    // that faulted, which only a defect can make it, does not keep the arena from being freed. The first
    // such failure is what the close then reports, once everything is freed.
    private async Task FreeAfterOperationsAsync(Task operationsEnded, long mark, TaskCompletionSource closed)
    {
        Exception? failure = null;

        await operationsEnded.ConfigureAwait(false);

        ChildListener? listener;
        MasterLink? master;
        Task? loaded;
        Task? following;

        lock (_stateLock)
        {
            listener = _listener;
            loaded = _loaded?.Task;
        }

        try
        {
            if (listener is not null)
            {
                await listener.StopAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            if (loaded is not null)
            {
                await loaded.ConfigureAwait(false);
            }

            // The follower is set before the open ends, so it is known once the open has ended; the close
            // ended its waits when it began, and its task ends at once.
            lock (_stateLock)
            {
                master = _master;
                following = _following;
            }

            if (following is not null)
            {
                await following.ConfigureAwait(false);
            }

            master?.Dispose();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            if (_epochs.HasReadsBefore(mark))
            {
                await Task.Run(() => _epochs.WaitForReadsBefore(mark, CancellationToken.None)).ConfigureAwait(false);
            }

            // The parts are set only when the open published them; an open that the close overtook
            // frees its own arena (Build). The arena is the only native memory of the database;
            // everything else is managed.
            //
            // The last drain frees what is retired, and with the roots it frees go their references to
            // text entries (Storage.Text.TextTable): a change of a count, which runs only on the thread
            // that holds the lock of the writes (the planner's decision after the study of S13, point 6).
            // Nothing can contend for the lock here — the barrier has let every write, drain, count and
            // placement end and admits none, the network has stopped, and the reads have left — so the
            // close takes it all the same, to keep that rule without an exception, and frees the arena
            // under it.
            if (_arena is { } arena)
            {
                lock (_writes!)
                {
                    _retired!.Drain();
                    _retired.Clear();
                    arena.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        if (failure is null)
        {
            closed.SetResult();
        }
        else
        {
            closed.SetException(failure);
        }
    }

    // A failed open completes once the close it began has completed, so that the instance leaves nothing
    // behind when the call fails.
    private static async Task FailAfterAsync(ValueTask closing, Exception exception)
    {
        await closing.ConfigureAwait(false);

        ExceptionDispatchInfo.Throw(exception);
    }

    // The open of a master (see OpenAsync). It passes the barrier of the close, so that a close that
    // begins meanwhile finds the listener it starts (FreeAfterOperationsAsync). A close that came while
    // it opened wins: the open fails with ObjectDisposedException.
    private void OpenMaster()
    {
        // Validate has checked its form: lattice://host:port.
        var listenUri = _options.ListenUri;
        var schema = _options.Schema!;

        _barrier.Enter();

        try
        {
            // A new history at every open: nothing of a MemoryOnly database survives it, so no position
            // of an earlier run may be taken for one of this run.
            Build(schema, (ulong)Random.Shared.NextInt64(1, long.MaxValue), child: false);

            if (listenUri is not null)
            {
                // A new identity of the database at every open, for the same reason; its children are
                // told it in the handshake (the planner's block of 15 November 2026, "Identity").
                var hello = HelloCheck.Of(Guid.NewGuid(), _clock!.History, schema.Fingerprint, LatticeRoleEnum.Master);
                var tables = schema.EntityTypes.Select(entityType => _operations![entityType].Table).ToArray();
                var capture = new Capture(_arena!, _roots!, _epochs, _clock, _writes!, _delivery!, tables, hello, SchemaTransfer.Saved(schema));
                var forwarded = new ForwardedWrites(_coordinator!, tables, _arena!, schema);
                var listener = Listen(listenUri, capture, forwarded);

                lock (_stateLock)
                {
                    _listener = listener;
                }
            }
        }
        finally
        {
            _barrier.Exit();
        }

        lock (_stateLock)
        {
            if (_state == LatticeDatabaseStateEnum.Closed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            _state = LatticeDatabaseStateEnum.Ready;
        }
    }

    // Starts the listener of a master at its address. A master that cannot listen there, because the
    // address is in use for one, fails its open with InvalidConfiguration, whose message gives the reason
    // the system reports (settled by the planner with the contract commit of S12).
    private ChildListener Listen(Uri listenUri, Capture capture, ForwardedWrites forwarded)
    {
        try
        {
            return ChildListener.Start(listenUri, capture, forwarded, _options.OpenTimeout);
        }
        catch (SocketException exception)
        {
            throw new LatticeException(LatticeErrorEnum.InvalidConfiguration, $"The master cannot listen at {listenUri}: {exception.Message}", exception);
        }
    }

    // The open of a child (see the class, "Master and children"). Every failure closes the instance, and
    // the call completes once the close has completed.
    private async Task OpenChildAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadFromMasterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);

            var failure = OpenFailure(exception, cancellationToken);

            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }
    }

    // Connects to the master, loads its database and becomes Ready. The task ends when the open has
    // ended, whatever ended it, and only then: the close waits for it (_loaded).
    private async Task LoadFromMasterAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
            var token = opening.Token;
            // Validate has checked its form: lattice://host:port, with a port other than 0.
            var masterUri = _options.MasterUri!;

            // A child holds nothing before it loads: an empty identity, history zero, an empty
            // fingerprint (brief of S10, HelloBody).
            var hello = HelloCheck.Of(Guid.Empty, 0, default, LatticeRoleEnum.Child);
            var master = await MasterLink.ConnectAsync(masterUri, _options.OpenTimeout, hello, token).ConfigureAwait(false);

            lock (_stateLock)
            {
                if (_state == LatticeDatabaseStateEnum.Closed)
                {
                    master.Dispose();
                    throw new ObjectDisposedException(GetType().FullName);
                }

                _master = master;
                _state = LatticeDatabaseStateEnum.Loading;
            }

            var memoryLimit = MemoryLimitInForce();
            var (schema, counts) = await master.ReceiveSchemaAsync(memoryLimit, token).ConfigureAwait(false);

            // The schema has to be the one the handshake bound: the master announced its fingerprint in
            // its HelloReply (review round 1 of S10).
            if (schema.Fingerprint != master.Master.SchemaFingerprint)
            {
                throw new LatticeException(LatticeErrorEnum.Unavailable, $"The master at {masterUri} sent a schema whose fingerprint {schema.Fingerprint} is not the {master.Master.SchemaFingerprint} its HelloReply announced.");
            }

            Build(schema, master.Master.History, child: true);

            var tables = schema.EntityTypes.Select(entityType => _operations![entityType].Table).ToArray();
            var changes = new ReceivedChanges(_arena!, _roots!, _executor!, schema, _epochs.Fault);

            PresizeIndexes(tables, counts, memoryLimit);

            var end = await master.ReceiveRootsAsync(new RootPlacement(_writes!, _executor!, _barrier, changes, Fault, tables), memoryLimit, token).ConfigureAwait(false);

            // The transactions of (S, R] follow LoadEnd: the child is Ready once it has applied R.
            var follower = new Follower(master, _forwarder!, _clock!.History, _writes!, _executor!, _barrier, changes, Fault, tables, memoryLimit, _options.OpenTimeout);

            var catchUp = await follower.CatchUpAsync(end, token).ConfigureAwait(false);

            lock (_stateLock)
            {
                if (_state == LatticeDatabaseStateEnum.Closed)
                {
                    throw new ObjectDisposedException(GetType().FullName);
                }

                CatchUp = catchUp;
                _follower = follower;
                _following = Task.Run(() => follower.FollowAsync(_closing.Token));
                _state = LatticeDatabaseStateEnum.Ready;
            }
        }
        finally
        {
            _loaded!.SetResult();
        }
    }

    // Sizes the primary index of each entity type for the roots its master announced (the planner's
    // decision of 17 November 2026 after the study of S10, point 6). A count of roots of a type the
    // engine does not store is not valid; counts that need more than the memory limit, each root at
    // least one block of its fixed part, cannot be held, and the open ends with Unavailable.
    private void PresizeIndexes(EntityTable[] tables, long[] counts, long memoryLimit)
    {
        var least = 0.0;

        for (var index = 0; index < tables.Length; index++)
        {
            if (counts[index] == 0)
            {
                continue;
            }

            if (tables[index].Layout is not { } layout)
            {
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The master announces {counts[index]} roots of {tables[index].EntityType.Name}, which the engine does not store.");
            }

            least += (double)counts[index] * Arena.BlockSizeFor(layout.FixedSize - BlockHeader.Size);
        }

        if (least > memoryLimit)
        {
            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The master announces roots that take at least {least:F0} bytes, more than the memory limit of this child, {memoryLimit} bytes: the child cannot hold the database of its master.");
        }

        lock (_writes!)
        {
            for (var index = 0; index < tables.Length; index++)
            {
                if (counts[index] > 0)
                {
                    tables[index].Presize(counts[index]);
                }
            }
        }
    }

    // What the open of a child reports for what ended it (the planner's block of 15 November 2026, "What
    // the child's caller sees", and what he settled with the tests of S12): its own token, and a close
    // that came while the open waited, OperationCanceledException — the caller has no instance yet that
    // could be unavailable, and closing it withdraws the request to open; an address it cannot use,
    // InvalidConfiguration; everything that ends the open because of the master, Unavailable with the
    // reason in the message.
    private Exception OpenFailure(Exception exception, CancellationToken cancellationToken)
    {
        var masterUri = _options.MasterUri;

        return exception switch
        {
            OperationCanceledException when cancellationToken.IsCancellationRequested => exception,
            OperationCanceledException or ObjectDisposedException when _closing.IsCancellationRequested =>
                new OperationCanceledException("The instance was closed while it opened.", exception),
            LatticeException { Error: LatticeErrorEnum.Unavailable or LatticeErrorEnum.InvalidConfiguration } => exception,
            LatticeException { Error: LatticeErrorEnum.ValidationFailed or LatticeErrorEnum.InvalidSchema } invalid =>
                new LatticeException(LatticeErrorEnum.Unavailable, $"What the master at {masterUri} sent is not valid: {invalid.Message}", invalid),
            TimeoutException => new LatticeException(LatticeErrorEnum.Unavailable, $"Nothing arrived from the master at {masterUri} for the open timeout of {_options.OpenTimeout}.", exception),
            IOException or SocketException => new LatticeException(LatticeErrorEnum.Unavailable, $"The connection to the master at {masterUri} ended before the load was complete.", exception),
            _ => exception,
        };
    }

    // The memory budget is the limit of the arena (see the class): the option, or half of the memory of
    // the process as .NET reports it, and never less than one segment (the owner's decision O17 and
    // answer to Q37), unless a test gave the options of the arena, which are then the options of the
    // arena and the option is not read.
    private long MemoryLimitInForce() =>
        _testInputs?.Arena?.MemoryLimit
        ?? _options.MemoryLimit
        ?? Math.Max(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2, LatticeDatabaseOptions.ArenaSegmentSize);

    // Builds the parts of an empty database of the schema in the history given, and publishes them, with
    // the forwarder of its writes on a child; the caller makes the instance Ready. A close that came meanwhile wins: what was built is freed, and the
    // open fails with ObjectDisposedException.
    private void Build(LatticeSchema schema, ulong history, bool child)
    {
        // The seed of the hashes of the primary indexes and of the text table, drawn at every open from
        // the random source of the system unless a test fixed it, so that nobody who does not know it can
        // choose keys or texts that share a hash (the planner's answer to Q50). It is never stored, sent or
        // shown: a child and a reopened database draw their own, and the order of an enumeration, which
        // follows the hashes, may differ between two instances that hold the same data.
        var seed = _testInputs?.IndexSeed ?? DrawSeed();
        var writes = new Lock();
        var arenaOptions = _testInputs?.Arena ?? new ArenaOptions { MemoryLimit = MemoryLimitInForce() };
        var arena = new Arena(arenaOptions, _testInputs?.Allocator ?? new NativeAllocator(), seed, writes);

        try
        {
            var roots = new RootTable();
            var retired = new RetiredMemory(_epochs, arena, roots);
            var clock = new PositionClock(history, this);
            var delivery = new DeliveryTail();
            var executor = new WriteExecutor(arena, roots, retired, clock, _epochs, delivery, schema);
            var coordinator = new WriteCoordinator(
                writes,
                executor,
                _testInputs?.CommitStorage ?? new MemoryOnlyStorage(),
                _barrier,
                clock,
                _options.SynchronizationTimeout,
                Fault);
            var forwarder = child ? new Forwarder(clock, _options.SynchronizationTimeout) : null;
            var operations = new Dictionary<LatticeEntityType, SetOperations>();
            var recordSets = new Dictionary<LatticeEntityType, RecordSet>();

            foreach (var entityType in schema.EntityTypes)
            {
                var table = EntityTable.Open(entityType, schema, arena, roots, _epochs, retired, clock, seed);
                var forType = new SetOperations(table, coordinator, forwarder, clock, _options);

                operations.Add(entityType, forType);
                recordSets.Add(entityType, new RecordSet(forType));
            }

            lock (_stateLock)
            {
                if (_state == LatticeDatabaseStateEnum.Closed)
                {
                    throw new ObjectDisposedException(GetType().FullName);
                }

                _schema = schema;
                _arena = arena;
                _roots = roots;
                _retired = retired;
                _clock = clock;
                _writes = writes;
                _coordinator = coordinator;
                _executor = executor;
                _delivery = delivery;
                _forwarder = forwarder;
                _operations = operations;
                _recordSets = recordSets;
            }
        }
        catch
        {
            // Nothing was published; the arena has no segment yet, so this frees nothing that a reader
            // could reach.
            arena.Dispose();
            throw;
        }
    }

    // A publication failed (WriteCoordinator, "A fault"; on a child Loading.ReceivedChanges): the instance
    // is Faulted unless a close came first. The reads are marked first, so that a read that retries on the
    // mark of the write that failed gives up at once, then the state, then the waits for a position end.
    // Called after the lock of the writes, since it takes the lock of the state; a child marks the reads
    // under the lock of the writes already (ReadEpochs.Fault, which takes no lock) and calls this after
    // it.
    private void Fault()
    {
        _epochs.Fault();

        lock (_stateLock)
        {
            if (_state == LatticeDatabaseStateEnum.Ready)
            {
                _state = LatticeDatabaseStateEnum.Faulted;
            }
        }

        _clock!.Fault();
    }

    // 64 bits from the random source of the system (RandomNumberGenerator).
    private static ulong DrawSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];

        RandomNumberGenerator.Fill(bytes);

        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private ClassSet<TEntity> ClassSetOf<TEntity>()
        where TEntity : class
    {
        var binding = EntityBinding<TEntity>.For(_schema!);

        return new ClassSet<TEntity>(_operations![binding.EntityType], binding);
    }

    private void ThrowIfNotReady()
    {
        var state = _state;

        ObjectDisposedException.ThrowIf(state == LatticeDatabaseStateEnum.Closed, this);

        if (state != LatticeDatabaseStateEnum.Ready)
        {
            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The database is {state}, not Ready.");
        }
    }
}
