using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Child;
using Moltaro.Lattice.Engine.Child.Following;
using Moltaro.Lattice.Engine.Child.Forwarding;
using Moltaro.Lattice.Engine.Child.Loading;
using Moltaro.Lattice.Engine.Children;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Sets;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine;

// The database of the engine: an ILatticeDatabase over the native arena, the table of root entries and the
// root layouts of this assembly, with the primary and the secondary indexes. It is what the public contract
// runs on: LatticeDatabase opens it. It is a MemoryOnly master that inserts, reads, replaces, upserts and
// deletes entities by primary key and searches them by an index, by class and by name, for many callers at
// once through its write coordinator, and that accepts children at a ListenUri; or a child that loads the
// whole database of its master over TCP, serves its reads, follows the writes of its master, and forwards
// its own writes to its master, each returning once this child has applied it. This type holds the life of
// an instance, its states, its open and its close, and hands every operation to its parts; each member says
// what it does, the parts what they guarantee.
//
// What the parts do, and where. The entity types and their reads are in Entities (EntityTable, one for
// each entity type, with its root layout), the text entries of Dictionary strings in the arena
// (Storage.Text.TextTable, which only writes use), the primary indexes in Indexes (PrimaryIndex), the
// writes in Writes (WriteCoordinator, which runs them under the lock of the writes and groups them for
// the persistence step, and WriteExecutor, which prepares and publishes them), the protection of reads
// by epochs, what is retired and the barrier of the close in Protection, the history and the sequence in
// Positions (PositionClock), the sets of the public interfaces in Sets, what a master gives its children
// in Children, the tail of the transactions it holds for them in Delivery, how a child loads in Loading,
// how it follows its master in Following, and how it forwards its writes in Forwarding. The parts of an
// open database are made together and held as one (DatabaseParts); the network of a master and of a
// child is kept beside them. What only the tests of the engine and the measurement program reach is in
// EngineDatabase.TestAccess.cs.
//
// The order of the locks and of the lifetimes. The database has two locks: the lock of the state,
// for an open, a close and a fault that race; and the lock of the writes (WriteCoordinator, which says
// what it protects), under which writes are admitted, prepared, handed to the persistence step and
// published, under which a drain or a count of the counters runs, under which the capture of a child
// starts and ends and its acknowledgments release the tail on a master, and under which a root is
// placed and a transaction applied on a child, and under which a maintenance step runs. It is held
// for synchronous work in memory only. The lock of the state is taken alone. No thread that holds a
// lock takes the other one. The listener of a master
// has a lock of its own for its connections; the follower of a child has one for its suspensions, its
// sender one for its queue and the table of its pending requests one; each is taken alone as well. No
// lock is held while bytes are sent or received.
// A read takes none: it enters the protection of reads, which is no lock and never waits for the writer, and
// nothing is taken inside it. What a read can reach lives at least as long as the read: a write unlinks it
// and retires it, and the writer frees it only once the reads that entered before have left (RetiredMemory);
// the close frees the arena only after the reads and the operations counted by CloseBarrier have
// ended. No code of the application runs, and nothing waits, while a read is inside.
//
internal sealed partial class EngineDatabase : ILatticeDatabase, IWriteBatchExecutor
{
    private readonly LatticeDatabaseOptions _options;

    // Guards the changes of the state: an open and a close that race.
    private readonly Lock _stateLock = new();

    private readonly CloseBarrier _barrier;

    private readonly ReadEpochs _epochs;

    // What a test of the engine gave besides the options (the second Create); null for every other
    // instance, which uses what each null member of EngineTestInputs stands for.
    private readonly EngineTestInputs? _testInputs;

    // The state. A master is Created, Loading during OpenAsync, Ready, Faulted, Closed. A child goes
    // Connecting, Loading, CatchingUp, Ready (docs/decisions/replication-and-hosting.md, option A): it is
    // Connecting while it has no connection to its master, the pauses between its attempts included,
    // Loading from the accepted handshake until the last root of its load is placed, and CatchingUp from
    // then until it has applied the transactions up to its readiness position R. Before Ready every
    // operation is Unavailable, after a close it is ObjectDisposedException, and once the instance is
    // Faulted (Fault) every operation, reads included, is Unavailable. Changed under the lock of the state,
    // read without it. State also observes the fault mark of the reads before the transition is stored here.
    private volatile LatticeDatabaseStateEnum _state = LatticeDatabaseStateEnum.Created;

    // The close, once a call has begun it: completes when the arena has been freed. Set under the lock
    // of the state by the call that sets Closed, and never changed afterwards.
    private TaskCompletionSource? _closed;

    // The parts of the open database (DatabaseParts). Published by the open before the state becomes
    // Ready, which a member reads first, and let go of by the close once it has freed the arena
    // (FreeAfterOperationsAsync); a member reads it once (Part).
    private DatabaseParts? _parts;

    // The network of the instance, set under the lock of the state and let go of only by the close: the
    // listener of a master with a ListenUri, set by the open while it is inside the barrier of the close,
    // so that a close that begins meanwhile finds it once the open has left the barrier; and on a child,
    // the connection to its master, set when the master accepted the child, and the end of the task of
    // its open, made when the open begins.
    private ChildListener? _listener;

    private MasterLink? _master;

    private TaskCompletionSource? _loaded;

    // On a child that is Ready: how it follows its master, and the task that does, which the close waits
    // for. Set under the lock of the state before the state becomes Ready, and let go of only by the
    // close.
    private Follower? _follower;

    private Task? _following;

    // Registered at Ready under the state lock. Only admitted turns touch the parts; close
    // joins the cadence outside that lock before returning the arena.
    private readonly AutomaticMaintenance _maintenance;

    private readonly Action<DatabaseParts> _runMaintenance;

    private Task? _maintaining;

    // Cancelled when close begins: ends the open of a child and the maintenance wait.
    private readonly CancellationTokenSource _closing = new();
    private readonly MemoryBudget _budget;

    // An instance for the options, which Check has accepted, without opening it: LatticeDatabase.Create, and
    // the Create of the tests with their inputs.
    internal EngineDatabase(LatticeDatabaseOptions options, EngineTestInputs? testInputs)
    {
        _options = options;
        _testInputs = testInputs;
        _budget = new MemoryBudget(testInputs is not null);
        _barrier = new CloseBarrier(this);
        _epochs = new ReadEpochs(this);
        _maintenance = new AutomaticMaintenance(
            testInputs?.BeforeTextMigrationPageAllocation, testInputs?.BeforeRootReclamationAllocation);
        _runMaintenance = _maintenance.Run;
    }

    // The refusals of LatticeDatabase.Create, in their order, and of the Create of the tests:
    // ArgumentNullException for null options; Validate, which gives InvalidConfiguration, also for an address
    // that does not have the form of the transport; then NotSupportedException for Durable persistence, which
    // is not built yet. An instance is made without being opened; a master that cannot listen at its address
    // fails its open (Listen).
    internal static void Check(LatticeDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.PersistenceMode == LatticePersistenceModeEnum.Durable)
        {
            throw new NotSupportedException("Durable persistence is not implemented yet in the engine. Select MemoryOnly explicitly.");
        }
    }

    public LatticeDatabaseStateEnum State
    {
        get
        {
            // Read the fault mark first so a close that preceded the mark still takes precedence.
            var faulted = _epochs.IsFaulted;
            var state = _state;

            return faulted && state == LatticeDatabaseStateEnum.Ready ? LatticeDatabaseStateEnum.Faulted : state;
        }
    }

    public LatticeSchema Schema
    {
        get
        {
            ThrowIfNotReady();

            return Part(_parts).Schema;
        }
    }

    public LatticePosition Position
    {
        get
        {
            ThrowIfNotReady();

            return Part(_parts).Clock.Position;
        }
    }

    // The address the master listens at, with the port the system chose for port 0; null for a master
    // without ListenUri and for a child.
    public Uri? ListenUri
    {
        get
        {
            ThrowIfNotReady();

            return _options.ListenUri is null ? null : Part(_listener).Address;
        }
    }

    // Served on a master and on a child: a master has applied every position it gave out; a child has applied
    // every position of its history up to the one it is at, and a wait for a later one ends when it applies
    // it or at its timeout, and at once on a child that has lost its master (Positions.PositionClock). It
    // throws at the call only a mistake of its arguments; the state of the database, a close, a fault,
    // another history and the loss of the master come through its task, also when no wait is needed.
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

        return WaitAfterChecksAsync(position, timeout ?? _options.SynchronizationTimeout, cancellationToken);
    }

    public ValueTask WaitForPositionAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        return WaitAfterChecksAsync(session, cancellationToken);
    }

    // The wait of WaitForPositionAsync for a position, once the call has checked its arguments: every
    // failure, also one that needs no wait, comes through the task (WaitForPositionAsync). In a Release build
    // an asynchronous method allocates nothing when it completes within its call: its state machine is a
    // struct on the stack. A Debug build makes the state machine a class and allocates it at every call.
    private async ValueTask WaitAfterChecksAsync(LatticePosition position, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfNotReady();
        await Part(_parts).Clock.WaitAsync(position, timeout, cancellationToken).ConfigureAwait(false);
    }

    // The same for the position a session depends on, with the timeout of the session or of the options.
    private async ValueTask WaitAfterChecksAsync(LatticeSession session, CancellationToken cancellationToken)
    {
        ThrowIfNotReady();
        await Part(_parts).Clock.WaitAsync(session, _options.SynchronizationTimeout, cancellationToken).ConfigureAwait(false);
    }

    // Opens the database. A master compiles the root layout of every entity type, makes the parts of an
    // empty database, listens at its ListenUri when it has one, and becomes Ready: its work is
    // synchronous and short, so it completes before it returns. A child loads the database of its
    // master (OpenChildAsync). The root layout of every entity type is compiled before any data is
    // admitted. A fixed part larger than one block fails the open with InvalidSchema. A failure or cancellation closes the
    // instance, and the call completes once the close has. Before the open an operation is Unavailable.
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
            _budget.Open(MemoryLimitInForce());

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
        var parts = Part(_parts);

        return (ILatticeSet<TEntity>)parts.ClassSets.GetOrAdd(typeof(TEntity), static (_, parts) => ClassSetOf<TEntity>(parts), parts);
    }

    public ILatticeSet Set(string entityName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityName);
        ThrowIfNotReady();

        var parts = Part(_parts);

        return parts.Schema.TryGetEntityType(entityName, out var entityType)
            ? parts.RecordSets[entityType]
            : throw new LatticeException(LatticeErrorEnum.BindingFailed, $"The schema has no entity type '{entityName}'.");
    }

    // The batch of the core, whose members this database encodes as they are added (EncodeRoot) and executes
    // when it is committed (ExecuteAsync).
    public ILatticeWriteBatch CreateWriteBatch()
    {
        ThrowIfNotReady();

        return new WriteBatch(Part(_parts).Schema, this, _budget);
    }

    // The root of a member of a write batch, encoded when it is added, on the thread of the caller and
    // outside every lock (Entities.RootValues.Export). A collection above its bound is refused at Add,
    // as it is before a single write is submitted (RootValues.CheckCollectionBounds). A delete carries
    // an empty root body.
    // ObjectDisposedException once the database is closed, for every kind of member.
    bool IWriteBatchExecutor.IsClosed => IsClosed;

    ReadOnlySequence<byte> IWriteBatchExecutor.EncodeRoot(LatticeEntityType entityType, object?[]? values) =>
        EncodeBatchRoot(entityType, values, null);

    ReadOnlySequence<byte> IWriteBatchExecutor.EncodeRoot(LatticeEntityType entityType, object?[]? values, MemoryLease? lease) =>
        EncodeBatchRoot(entityType, values, lease);

    private ReadOnlySequence<byte> EncodeBatchRoot(LatticeEntityType entityType, object?[]? values, MemoryLease? lease)
    {
        var table = Part(_parts).Operations[entityType].Table;

        if (values is null)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        var layout = table.Layout;
        if (layout.Structure.HoldsCollections)
        {
            RootValues.CheckCollectionBounds(layout, values);
        }

        return RootValues.Export(layout, entityType, values, lease);
    }

    void IWriteBatchExecutor.CheckUpdate(LatticeEntityType entityType, UpdateInput update)
    {
        ThrowIfNotReady();
        RootValues.BindUpdate(Part(_parts).Operations[entityType].Table.Layout, update);
    }

    // The commit of a write batch of the core: its members, each key in the form of its entity type
    // (Sets.KeyArgument), go on a master to the write coordinator as one write
    // (Writes.WriteCoordinator.SubmitBatchAsync), and on a child to its master as one request
    // (Child.Forwarding.Forwarder.WriteBatchAsync). The close, and every refusal, come through the task.
    ValueTask<LatticeWriteResult> IWriteBatchExecutor.ExecuteAsync(IReadOnlyList<WriteBatchOperation> operations, LatticeSession? session, CancellationToken cancellationToken)
    {
        var parts = _parts;

        if (parts is null)
        {
            return ValueTask.FromException<LatticeWriteResult>(new ObjectDisposedException(GetType().FullName));
        }

        return ExecuteBatchAsync(parts, operations, session, cancellationToken);
    }

    private async ValueTask<LatticeWriteResult> ExecuteBatchAsync(DatabaseParts parts, IReadOnlyList<WriteBatchOperation> operations,
        LatticeSession? session, CancellationToken cancellationToken)
    {
        BatchMember[]? members = null;
        try
        {
            members = _budget.Allocate<BatchMember>(operations.Count, nameof(EngineDatabase));

            for (var place = 0; place < members.Length; place++)
            {
                var operation = operations[place];
                var table = parts.Operations[operation.EntityType].Table;

                var key = operation.Update is null ? KeyArgument.Of(table, operation.Key) : KeyArgument.OfNormalized(table, operation.Key);
                members[place] = new BatchMember(operation.Kind, table, key, operation.ExpectedRevision, operation.Root, operation.Update);
            }

            var result = parts.Forwarder is { } forwarder
                ? forwarder.WriteBatchAsync(members, session, cancellationToken)
                : parts.Coordinator.SubmitBatchAsync(members, session, cancellationToken);
            return await result.ConfigureAwait(false);
        }
        catch (BudgetRefusedException exception)
        {
            throw exception.WriteRejected();
        }
        finally
        {
            if (members is not null)
            {
                // A caller may retain the completed task and its lifted local. End its
                // ownership of the batch roots before the task publishes the result.
                Array.Clear(members);
                _budget.Release(members);
            }
        }
    }

    // Lets no operation start any more, ends the waits for a position, lets every write admitted before it
    // reach its outcome, and frees the arena once nothing can reach it any more; then it lets go of the
    // parts of the database and of its network, so that an instance kept alive after its close holds none of
    // them. The close is one operation: the first call, of this or of DisposeAsync, begins it and keeps its
    // task, and every later call waits for that same task, so no call completes before the arena is freed,
    // whichever call it is; the synchronous Dispose blocks its thread until then. The cancellation token ends
    // only the wait of its call. The close covers the network: on a master it stops the listener once the
    // admitted writes have ended, each with its reply recorded when a child forwarded it
    // (Children.ChildListener says how its connections end); on a child it ends the open in progress and the
    // following of its master, waits for their tasks, and closes the connection to the master without
    // waiting for the master: its writes in flight end as the forwarding says (Child.Forwarding.Forwarder). A
    // suspension of the application held at the close does not keep it from completing.
    //
    // The steps, in the order of the code, here and in FreeAfterOperationsAsync:
    // 1. Under the lock of the state, make the close task and close admission. ReadEpochs.Close
    //    refuses new reads and, on a child, new submissions to the forwarder. A read that entered
    //    before the mark keeps its epoch, which step 8 waits for. On a master the close barrier
    //    refuses new submissions and tracks those already admitted until they reach their outcome.
    // 2. Under the same lock, publish Closed. This state decides pending position waits. A confirmed
    //    write through a child can now return its result because new reads, including those through
    //    existing sets, are refused.
    // 3. Outside the lock of the state, notify all position waiters (PositionClock.Close).
    // 4. The open of a child in progress is cancelled: it ends at its next wait, or at its next
    //    placement, which the barrier refuses.
    // 5. Close the child's barrier only now, so application of the master's changes can continue
    //    until the child is Closed. For either role, wait for the operations inside the barrier to end.
    // 6. The listener of a master stops (ChildListener.StopAsync): after step 5, because an open of a
    //    master is an operation while it starts its listener, and because a connection sends its child
    //    the replies of the admitted writes before it ends.
    // 7. The open of a child ends, then its following, then its connection to the master closes; the
    //    master is not waited for.
    // 8. The reads that entered before the mark leave: after the network, whose copies of roots read
    //    inside the protection and leave it once their connections end.
    // 9. Under the lock of the writes the last drain frees what is retired, and the arena is freed: no
    //    read and no write can reach it any more.
    // 10. The parts of the database are let go of, so that an instance kept alive holds none of them.
    // 11. The close completes, with the first failure of a step when one failed: every step runs
    //     whatever became of the one before.
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource closed;
        long? readMark = null;
        Task? masterOperationsEnded = null;

        lock (_stateLock)
        {
            if (_closed is null)
            {
                _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                readMark = _epochs.Close();

                if (_options.Role == LatticeRoleEnum.Master)
                {
                    masterOperationsEnded = _barrier.Close();
                }

                _state = LatticeDatabaseStateEnum.Closed;
            }

            closed = _closed;
        }

        if (readMark is { } mark)
        {
            _testInputs?.AfterCloseStatePublication?.Invoke();

            // Steps 3 to 5 (see above).
            _parts?.Clock.Close();

            _closing.Cancel();

            _ = FreeAfterOperationsAsync(masterOperationsEnded ?? _barrier.Close(), mark, closed);
        }

        var task = closed.Task;

        return task.IsCompleted ? new ValueTask(task) : new ValueTask(task.WaitAsync(cancellationToken));
    }

    public ValueTask DisposeAsync() => CloseAsync();

    public void Dispose() => CloseAsync().AsTask().GetAwaiter().GetResult();

    internal bool TryRunMaintenanceTurn()
    {
        if (_testInputs?.ObserveMaintenanceTurn is not { } observe)
        {
            return TryRunMaintenanceStep(_runMaintenance);
        }

        var started = Stopwatch.GetTimestamp();
        MaintenanceTurnSample sample = default;
        var admitted = TryRunMaintenanceStep(parts => sample = _maintenance.Step(parts));
        observe(sample with { Admitted = admitted, ElapsedTicks = Stopwatch.GetTimestamp() - started });
        return admitted;
    }

    // Counts the attempt before reaching the parts so that close cannot free them while it runs.
    // Runs the bounded action under the lock of the writes, without waiting for that lock.
    // Returns true once the action completes, false if it was skipped, and propagates its failure.
    internal bool TryRunMaintenanceStep(Action<DatabaseParts> step)
    {
        if (!_barrier.TryEnter())
        {
            return false;
        }

        try
        {
            if (State != LatticeDatabaseStateEnum.Ready)
            {
                return false;
            }

            _testInputs?.BeforeMaintenanceWrites?.Invoke();

            var parts = Part(_parts);
            if (!parts.Writes.TryEnter())
            {
                return false;
            }

            try
            {
                // State includes the child's fault mark. The master coordinator can mark its
                // own fault before the state observes it.
                if (State != LatticeDatabaseStateEnum.Ready ||
                    (_options.Role == LatticeRoleEnum.Master && parts.Coordinator.IsFaulted))
                {
                    return false;
                }

                step(parts);

                return true;
            }
            finally
            {
                parts.Writes.Exit();
            }
        }
        finally
        {
            _barrier.Exit();
        }
    }

    // Steps 6 to 11 of the close (CloseAsync), once the operations in progress have ended. When nothing
    // is in progress it runs to its end before it returns. The wait for the reads runs on a thread of the
    // pool, because a read may be held by a test while its caller goes on; it ends as
    // ReadEpochs.WaitForReadsBefore says. A task of the network that faulted, which only a defect can make
    // it, does not keep the arena from being freed; the first such failure is what the close reports, once
    // everything is freed.
    private async Task FreeAfterOperationsAsync(Task operationsEnded, long mark, TaskCompletionSource closed)
    {
        Exception? failure = null;

        await operationsEnded.ConfigureAwait(false);

        ChildListener? listener;
        MasterLink? master;
        Task? loaded;
        Task? following;
        Task? maintaining;

        lock (_stateLock)
        {
            listener = _listener;
            loaded = _loaded?.Task;
            maintaining = _maintaining;
        }

        try
        {
            if (maintaining is not null)
            {
                await maintaining.ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
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
            failure ??= exception;
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
            master?.ReleaseBacking();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        try
        {
            if (_epochs.HasReadsBefore(mark))
            {
                _testInputs?.BeforeCloseReadWait?.Invoke();
                await Task.Run(() => _epochs.WaitForReadsBefore(mark, CancellationToken.None)).ConfigureAwait(false);
            }

            // The parts are set only when the open published them; an open that the close overtook
            // frees its own arena (Build). The arena is the only native memory of the database;
            // everything else is managed.
            //
            // The last drain frees what is retired, and with the roots it frees go their references to
            // text entries (Storage.Text.TextTable): a change of a count, which runs only on the thread
            // that holds the lock of the writes (Storage.Text.TextTable).
            // The barrier has drained every counted operation, the network has stopped, and the
            // reads have left. Nothing can contend for the writes lock here. Close takes the lock
            // anyway so that the rule for changing the arena still holds.
            if (_parts is { } parts)
            {
                lock (parts.Writes)
                {
                    // Once the reads have left, a failure in one retirement step cannot keep the arena alive.
                    try
                    {
                        parts.Retired.DrainAtClose(_testInputs?.BeforeCloseRetiredTrim);
                    }
                    catch (Exception exception)
                    {
                        failure ??= exception;
                    }

                    try
                    {
                        parts.Retired.Clear();
                    }
                    catch (Exception exception)
                    {
                        failure ??= exception;
                    }

                    parts.Arena.Dispose();
                }
            }
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }

        // Nothing uses the parts any more: every member reaches them only once it has found the state
        // Ready, through the barrier, which admits nothing now, or through Part. So an instance kept
        // alive after its close holds none of them, nor its network.
        lock (_stateLock)
        {
            _parts = null;
            _listener = null;
            _master = null;
            _follower = null;
            _following = null;
            _maintaining = null;
        }

        _budget.Close();

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

    // The open of a master (see OpenAsync). A master with a ListenUri serves every node that connects from
    // its open on (Children.ChildListener); no write is refused or delayed because of a child
    // (Delivery.DeliveryTail). It passes the barrier of the close, so that a close that begins meanwhile
    // finds the listener it starts (FreeAfterOperationsAsync). A close that came while it opened wins: the
    // open fails with ObjectDisposedException.
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
            var parts = Build(schema, (ulong)Random.Shared.NextInt64(1, long.MaxValue), child: false);

            if (listenUri is not null)
            {
                // A new identity of the database at every open, for the same reason; its children are
                // told it in the handshake.
                var hello = HelloCheck.Of(Guid.NewGuid(), parts.Clock.History, schema.Fingerprint, LatticeRoleEnum.Master);
                var capture = new Capture(parts.Arena, parts.Roots, _epochs, parts.Clock, parts.Writes, parts.Delivery, parts.Tables, hello, SchemaTransfer.Saved(schema));
                var forwarded = new ForwardedWrites(parts.Coordinator, parts.Tables, parts.Arena);
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

            BecomeReady();
        }
    }

    // Starts the listener of a master at its address. A master that cannot listen there, because the
    // address is in use for one, fails its open with InvalidConfiguration, whose message gives the reason
    // the system reports.
    private ChildListener Listen(Uri listenUri, Capture capture, ForwardedWrites forwarded)
    {
        try
        {
            return ChildListener.Start(listenUri, capture, forwarded, _options.OpenTimeout, _testInputs);
        }
        catch (SocketException exception)
        {
            throw new LatticeException(LatticeErrorEnum.InvalidConfiguration, $"The master cannot listen at {listenUri}: {exception.Message}", exception);
        }
    }

    // The open of a child (LoadFromMasterAsync). Every failure, a timeout and a cancellation close the
    // instance, and the call completes once the close has completed: no connection, no task and no memory
    // of the open is left.
    private async Task OpenChildAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadFromMasterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);

            var failure = ChildLoad.OpenFailure(exception, cancellationToken, _closing.IsCancellationRequested, _options.MasterUri, _options.OpenTimeout);

            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }
    }

    // Connects to the master at MasterUri (Child.MasterLink), receives the schema, builds the parts of
    // an empty database with the history of its master, so that the revision of a loaded entity is its
    // revision on the master, places every root it receives under its own lock of the writes
    // (Child.Loading.ChildLoad, RootPlacement), applies the transactions of (S, R] by the same rule
    // (Child.Loading.ReceivedChanges, Child.Following.Follower), and becomes Ready at R. It then follows
    // its master, and tells it what it applied, until the connection ends or it cannot apply a
    // transaction; then it stops following and stays Ready at its position (reconnection is not built
    // yet). The task ends when the open has ended, whatever ended it, and only then: the close waits for
    // it (_loaded).
    private async Task LoadFromMasterAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
            var token = opening.Token;
            // Validate has checked its form: lattice://host:port, with a port other than 0.
            var masterUri = _options.MasterUri!;

            // A child holds nothing before it loads: an empty identity, history zero, an empty
            // fingerprint (Replication.HelloBody).
            var hello = HelloCheck.Of(Guid.Empty, 0, default, LatticeRoleEnum.Child);
            var master = await MasterLink.ConnectAsync(masterUri, _options.OpenTimeout, hello, _budget, token).ConfigureAwait(false);

            lock (_stateLock)
            {
                if (_state == LatticeDatabaseStateEnum.Closed)
                {
                    master.Dispose();
                    master.ReleaseBacking();
                    throw new ObjectDisposedException(GetType().FullName);
                }

                _master = master;
                _state = LatticeDatabaseStateEnum.Loading;
            }

            var memoryLimit = _budget.Limit;
            var (schema, counts) = await ChildLoad.ReceiveSchemaAsync(master, masterUri, memoryLimit, token).ConfigureAwait(false);
            var parts = Build(schema, master.Master.History, child: true);
            var changes = new ReceivedChanges(parts.Arena, parts.Roots, parts.Retired, parts.Clock, _epochs, parts.Tables, parts.Publisher,
                parts.Writes, _barrier, Fault, _testInputs?.BeforeReceivedRootListGrowth,
                beforePreparationMember: _testInputs?.BeforeChildPreparationMember);
            var end = await ChildLoad.ReceiveRootsAsync(master, changes, parts.Tables, parts.Roots, parts.Writes,
                counts, _budget, token).ConfigureAwait(false);

            // The load is complete; the transactions of (S, R] follow LoadEnd: the child is CatchingUp
            // until it has applied R, and then Ready.
            lock (_stateLock)
            {
                if (_state == LatticeDatabaseStateEnum.Closed)
                {
                    throw new ObjectDisposedException(GetType().FullName);
                }

                _state = LatticeDatabaseStateEnum.CatchingUp;
            }

            // The writes of the child go to its master through the sender of its connection, made here beside
            // the forwarder Build made for a child, which the follower attaches it to once the child has
            // caught up; their replies come back to the table of the calls sent.
            var pending = new PendingRequests(parts.Clock.History, _budget);
            var sender = new ChildSender(master, pending, _budget);
            var follower = new Follower(master, sender, pending, parts.Forwarder!, parts.Clock, changes, _budget,
                _options.OpenTimeout, _testInputs);

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
                BecomeReady();
            }
        }
        finally
        {
            _loaded!.SetResult();
        }
    }

    // Under the state lock: publish Ready and register its asynchronous worker before close can
    // take the lock. The worker captures neither the arena nor the writes lock while it waits.
    private void BecomeReady()
    {
        _state = LatticeDatabaseStateEnum.Ready;
        if (_testInputs?.DisableBackgroundMaintenance != true)
        {
            _maintaining = Task.Run(() => MaintainAsync(_closing.Token));
        }
    }

    private async Task MaintainAsync(CancellationToken cancellationToken)
    {
        // A missed tick can coalesce while a turn runs. Disposing wakes a parked wait on close.
        using var cadence = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        using var wakeOnClose = cancellationToken.UnsafeRegister(static timer => ((PeriodicTimer)timer!).Dispose(), cadence);
        try
        {
            while (State == LatticeDatabaseStateEnum.Ready)
            {
                _testInputs?.BeforeMaintenanceWait?.Invoke(cancellationToken);
                if (!await cadence.WaitForNextTickAsync().ConfigureAwait(false))
                {
                    break;
                }

                TryRunMaintenanceTurn();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    // The memory budget is the limit of the arena, the native memory in which the entities are stored, with
    // the blocks that writes in progress prepared there and what writes retired until the reads that could
    // reach it have left: the option, or half of the memory of the process as .NET reports it, read at open,
    // and never less than one segment (docs/decisions/write-admission.md), unless a test gave the options of
    // the arena, which are then the options of the arena and the option is not read. It is fixed at open
    // (EngineCounters.MemoryLimit). A write the arena cannot hold is WriteRejected with Limit Memory, as the
    // write coordinator decides. The indexes, the root entries and what an enumeration remembers are not
    // counted yet. An entity has no size limit of its own (docs/decisions/write-admission.md, "Configurable
    // root-object size limit"); one String or Binary value above 268,173,376 bytes, the capacity of one
    // directory block of chunks, is ValidationFailed with the field named, a fixed rule of the storage
    // format, which the validation of the core checks before a write reaches the engine.
    private long MemoryLimitInForce() =>
        _testInputs?.Arena?.MemoryLimit
        ?? _options.MemoryLimit
        ?? Math.Max(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2, LatticeDatabaseOptions.ArenaSegmentSize);

    // Builds the parts of an empty database of the schema in the history given, with the forwarder of its
    // writes on a child, publishes them as one and returns them; the caller makes the instance Ready. A
    // close that came meanwhile wins: what was built is freed, and the open fails with
    // ObjectDisposedException.
    //
    // What is live on a child. Its writes go to its master through the forwarder made here
    // (Sets.SetOperations) and the sender its open makes beside it; the coordinator made here admits no
    // write, and only its counters are read; what the child applies takes the lock of the writes itself
    // (Child.Loading.ReceivedChanges).
    private DatabaseParts Build(LatticeSchema schema, ulong history, bool child)
    {
        // The seed of the hashes of the primary indexes and of the text table, drawn at every open from the
        // random source of the system unless a test fixed it, so that nobody who does not know it can choose
        // keys or texts that share a hash. It is never stored, sent or shown: a child and a reopened database
        // draw their own, and the order of an enumeration, which follows the hashes, may differ between two
        // instances that hold the same data.
        var seed = _testInputs?.IndexSeed ?? DrawSeed();
        var writes = new Lock();
        var arenaOptions = _testInputs?.Arena ?? new ArenaOptions { MemoryLimit = _budget.Limit };
        var arena = new Arena(arenaOptions, _testInputs?.Allocator ?? new NativeAllocator(), seed, writes,
            _testInputs?.MaintenanceTimeProvider, _budget);

        try
        {
            var roots = RootTable.Create(_budget, _testInputs?.RootPageLimit);
            var retired = new RetiredMemory(_epochs, arena, roots, _testInputs?.BeforePublishedRetiredTrim,
                _testInputs?.ObserveRetirementBackingRefusal);
            var clock = new PositionClock(history, this, _testInputs?.PositionWaitTime, _epochs,
                isDatabaseClosed: () => IsClosed);
            var delivery = new DeliveryTail();
            var tables = new EntityTables([.. schema.EntityTypes.Select(entityType => EntityTable.Open(entityType, schema, arena, roots, _epochs, retired, clock, seed))]);
            var changes = new ChangePublisher(arena, roots, retired, clock, tables);
            if (!child)
            {
                changes.TrackPreparedWorkspaces();
            }

            var executor = new WriteExecutor(arena, roots, retired, clock, changes, delivery);
            var coordinator = new WriteCoordinator(
                writes,
                executor,
                changes,
                retired,
                _epochs,
                delivery,
                _testInputs?.Persistence ?? new MemoryOnlyPersistence(),
                _barrier,
                clock,
                _options.SynchronizationTimeout,
                Fault);
            var forwarder = child ? new Forwarder(clock, _epochs, _options.SynchronizationTimeout, _budget) : null;
            var reads = new ReadSettings(_options, _testInputs?.ReadTimestamps);
            var views = new ResidentViews(arena, schema.StringEncoding);
            var operations = new Dictionary<LatticeEntityType, SetOperations>();
            var recordSets = new Dictionary<LatticeEntityType, RecordSet>();

            foreach (var table in tables.InSchemaOrder)
            {
                var forType = new SetOperations(this, table, coordinator, forwarder, clock, _options, reads, views);

                operations.Add(table.EntityType, forType);
                recordSets.Add(table.EntityType, new RecordSet(forType));
            }

            var parts = new DatabaseParts
            {
                Schema = schema,
                Arena = arena,
                Roots = roots,
                Retired = retired,
                Clock = clock,
                Writes = writes,
                Coordinator = coordinator,
                Publisher = changes,
                Delivery = delivery,
                Forwarder = forwarder,
                Tables = tables,
                Operations = operations,
                RecordSets = recordSets,
            };

            lock (_stateLock)
            {
                if (_state == LatticeDatabaseStateEnum.Closed)
                {
                    throw new ObjectDisposedException(GetType().FullName);
                }

                _parts = parts;
            }

            return parts;
        }
        catch
        {
            // Nothing was published; the arena has no segment yet, so this frees nothing that a reader
            // could reach.
            arena.Dispose();
            throw;
        }
    }

    // A publication failed (WriteCoordinator, "A fault"; on a child Child.Loading.ReceivedChanges): the
    // instance is Faulted unless a close came first. The mark makes the fault visible to reads and to
    // State at once. Then the stored state changes and the waits for a position end. Called after the
    // lock of the writes, since it takes the lock of the state. A child marks the fault under the lock
    // of the writes already (ReadEpochs.Fault, which takes no lock) and calls this after it. The writes
    // in progress end as the write coordinator says, and a close works. A MemoryOnly instance that
    // faulted has lost its data.
    private void Fault()
    {
        _testInputs?.BeforeFaultNotification?.Invoke();

        if (_epochs.Fault())
        {
            Part(_parts).Publisher.OfferFaultSwitch();
        }

        lock (_stateLock)
        {
            if (_state == LatticeDatabaseStateEnum.Ready)
            {
                _state = LatticeDatabaseStateEnum.Faulted;
            }
        }

        Part(_parts).Clock.Fault();
    }

    // 64 bits from the random source of the system (RandomNumberGenerator).
    private static ulong DrawSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];

        RandomNumberGenerator.Fill(bytes);

        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static ClassSet<TEntity> ClassSetOf<TEntity>(DatabaseParts parts)
        where TEntity : class
    {
        var binding = EntityBinding<TEntity>.For(parts.Schema);

        return new ClassSet<TEntity>(parts.Operations[binding.EntityType], binding);
    }

    // The public close mark is set before the position clock notifies existing waiters.
    internal bool IsClosed => _state == LatticeDatabaseStateEnum.Closed;

    internal void ThrowIfNotReady()
    {
        var state = State;

        ObjectDisposedException.ThrowIf(state == LatticeDatabaseStateEnum.Closed, this);

        if (state != LatticeDatabaseStateEnum.Ready)
        {
            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The database is {state}, not Ready.");
        }
    }

    // After ThrowIfNotReady, for a member that reaches the parts of the open database or its network: the
    // part, read once, which is null only once the close has let go of it, and the state was Closed before
    // that (FreeAfterOperationsAsync). So a member that read the state Ready just before a close that then
    // ran to its end reports ObjectDisposedException as every member after the close does; inside the
    // barrier of the close it is never null.
    private T Part<T>(T? part)
        where T : class
    {
        ObjectDisposedException.ThrowIf(part is null, this);

        return part;
    }
}
