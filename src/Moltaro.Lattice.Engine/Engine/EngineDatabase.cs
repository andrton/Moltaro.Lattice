using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Sets;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine;

// The database of the engine: an ILatticeDatabase over the native arena, the table of root
// entries and the root layouts of this assembly, with the primary index. It is what the public
// contract runs on once the placeholder of the core library is gone. It is a MemoryOnly master that
// inserts, reads, replaces, upserts and deletes entities of scalar fields by primary key, by class
// and by name (brief of Epic 1, sprint S06), for many callers at once through its write coordinator
// (S09).
//
// How a test reaches it. Until S12 LatticeDatabase still opens the placeholder, and a test opens
// the engine through Create, the internal entry of exception X4 (docs/work/exceptions.md): only
// the opening differs, and every operation after it goes through the public interfaces. In S12
// LatticeDatabase returns this class, and Create is removed with its uses.
//
// Create. An instance is a MemoryOnly master. Create makes it without opening it and refuses in the
// order of LatticeDatabase.Create: ArgumentNullException for null options; Validate, which gives
// InvalidConfiguration; then NotSupportedException for Durable persistence (until S27), for a child
// role (until S10) and for a ListenUri (until S10).
//
// States. Created, Loading during OpenAsync, Ready, Faulted, Closed; Connecting and CatchingUp do not
// occur. OpenAsync compiles the root layout of every entity type before any data is admitted: a fixed
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
// until then.
//
// What the parts do, and where. The entity types and their reads are in Entities (EntityTable, one
// for each entity type, with its root layout), the primary indexes in Indexes (PrimaryIndex), the
// writes in Writes (WriteCoordinator, which runs them under the lock of the writes and groups them
// for the storage step, and WriteExecutor, which prepares and publishes them), the protection of reads by epochs, what is retired and the barrier of the close in
// Protection, the history and the sequence in Positions (PositionClock), and the sets of the public
// interfaces in Sets.
//
// The order of the locks and of the lifetimes. The database has two locks: the lock of the state,
// for an open, a close and a fault that race; and the lock of the writes (WriteCoordinator, which says
// what it protects), under which writes are admitted, prepared, handed to the storage step and
// published, and under which a drain or a count of the counters runs, held for synchronous work in
// memory only. The lock of the state is taken alone; no thread that holds a lock takes the other one.
// A read takes none: it enters the protection of reads, which is no lock and never waits for the writer, and
// nothing is taken inside it. What a read can reach lives at least as long as the read: a write
// unlinks it and retires it, and the writer frees it only once the reads that entered before have
// left (RetiredMemory); the close frees the arena only after the reads and the writes in progress
// have ended. No code of the application runs, and nothing waits, while a read is inside.
//
// Position and revision. Each open has a new random non-zero history; the sequence is 0 and grows
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
// WaitForPositionAsync, are served: a master has applied every position it gave out.
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

    private Dictionary<LatticeEntityType, SetOperations>? _operations;

    private Dictionary<LatticeEntityType, RecordSet>? _recordSets;

    private EngineDatabase(LatticeDatabaseOptions options, EngineTestInputs? testInputs)
    {
        _options = options;
        _testInputs = testInputs;
        _barrier = new CloseBarrier(this);
        _epochs = new ReadEpochs(this);
    }

    // Makes an instance for the options without opening it: a MemoryOnly master.
    public static EngineDatabase Create(LatticeDatabaseOptions options)
    {
        Check(options);

        return new EngineDatabase(options, testInputs: null);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): makes an instance as the
    // first Create does, with the same refusals, that takes what the inputs give instead of what an
    // instance makes for itself: the options of its arena, the allocator of its native memory, the
    // seed of the hashes of its primary indexes and the storage step of its commit stage, each when it
    // is set (EngineTestInputs). When the inputs give the options of the arena, MemoryLimit of the
    // options of the database is not read. Nothing else differs, and an instance made by the first
    // Create pays nothing for it.
    public static EngineDatabase Create(LatticeDatabaseOptions options, EngineTestInputs inputs)
    {
        Check(options);
        ArgumentNullException.ThrowIfNull(inputs);

        return new EngineDatabase(options, inputs);
    }

    // The refusals of Create, in the order of LatticeDatabase.Create.
    private static void Check(LatticeDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        if (options.PersistenceMode == LatticePersistenceModeEnum.Durable)
        {
            throw new NotSupportedException("Durable persistence is not implemented yet in the engine. Select MemoryOnly explicitly.");
        }

        if (options.Role == LatticeRoleEnum.Child)
        {
            throw new NotSupportedException("A child is not implemented yet in the engine.");
        }

        if (options.ListenUri is not null)
        {
            throw new NotSupportedException("A master that accepts children at a ListenUri is not implemented yet in the engine.");
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
                        _arena.MemoryLimit);
                }
            }
            finally
            {
                _barrier.Exit();
            }
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

    // Opens the database: compiles the root layout of every entity type and makes the parts of an
    // empty database, then becomes Ready. Its work is synchronous and short, so it completes before
    // it returns. A failure, and a cancellation before it starts, close the instance.
    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
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

            _state = LatticeDatabaseStateEnum.Loading;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Load();
        }
        catch (Exception exception)
        {
            _ = CloseAsync(CancellationToken.None);

            return ValueTask.FromException(exception);
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

            _ = FreeAfterOperationsAsync(_barrier.Close(), mark, closed);
        }

        var task = closed.Task;

        return task.IsCompleted ? new ValueTask(task) : new ValueTask(task.WaitAsync(cancellationToken));
    }

    public ValueTask DisposeAsync() => CloseAsync();

    public void Dispose() => CloseAsync().AsTask().GetAwaiter().GetResult();

    // Frees the arena, and with it every block, the retired ones included, once the writes, drains and
    // counts of the counters in progress have ended and then the reads that entered before the mark of
    // the close, and then completes the close. When nothing is in progress it does so before it
    // returns. The wait for the reads runs on a thread of the pool, because a read may be held by a
    // test while its caller goes on; it ends as ReadEpochs.WaitForReadsBefore says.
    private async Task FreeAfterOperationsAsync(Task operationsEnded, long mark, TaskCompletionSource closed)
    {
        try
        {
            await operationsEnded.ConfigureAwait(false);

            if (_epochs.HasReadsBefore(mark))
            {
                await Task.Run(() => _epochs.WaitForReadsBefore(mark, CancellationToken.None)).ConfigureAwait(false);
            }

            // The parts are set only when the open published them; an open that the close overtook
            // frees its own arena (Load). The arena is the only native memory of the database;
            // everything else is managed.
            if (_arena is { } arena)
            {
                _retired!.Drain();
                _retired.Clear();
                arena.Dispose();
            }

            closed.SetResult();
        }
        catch (Exception exception)
        {
            closed.SetException(exception);
        }
    }

    // Builds the parts of the open database and makes it Ready. A close that came while it loaded
    // wins: what was built is freed, and the open fails with ObjectDisposedException.
    private void Load()
    {
        var schema = _options.Schema!;

        // The memory budget is the limit of the arena (see the class): the option, or half of the
        // memory of the process as .NET reports it, and never less than one segment (the owner's
        // decision O17 and answer to Q37), unless a test gave the options of the arena, which are then
        // the options of the arena and the option is not read.
        var arenaOptions = _testInputs?.Arena ?? new ArenaOptions
        {
            MemoryLimit = _options.MemoryLimit ?? Math.Max(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2, LatticeDatabaseOptions.ArenaSegmentSize),
        };
        var arena = new Arena(arenaOptions, _testInputs?.Allocator ?? new NativeAllocator());

        try
        {
            var roots = new RootTable();
            var retired = new RetiredMemory(_epochs, arena, roots);

            // A new history at every open: nothing of a MemoryOnly database survives it, so no
            // position of an earlier run may be taken for one of this run.
            var clock = new PositionClock((ulong)Random.Shared.NextInt64(1, long.MaxValue), this);

            // The seed of the hashes of the primary indexes, drawn at every open from the random source
            // of the system unless a test fixed it, so that nobody who does not know it can choose keys
            // that share a hash (the planner's answer to Q50). It is never stored, sent or shown: a
            // child and a reopened database draw their own, and the order of an enumeration, which
            // follows the hashes, may differ between two instances that hold the same data.
            var seed = _testInputs?.IndexSeed ?? DrawSeed();
            var writes = new Lock();
            var executor = new WriteExecutor(arena, roots, retired, clock, _epochs);
            var coordinator = new WriteCoordinator(
                writes,
                executor,
                _testInputs?.CommitStorage ?? new MemoryOnlyStorage(),
                _barrier,
                clock,
                _options.SynchronizationTimeout,
                Fault);
            var operations = new Dictionary<LatticeEntityType, SetOperations>();
            var recordSets = new Dictionary<LatticeEntityType, RecordSet>();

            foreach (var entityType in schema.EntityTypes)
            {
                var table = EntityTable.Open(entityType, schema, arena, roots, _epochs, retired, clock, seed);
                var forType = new SetOperations(table, coordinator, clock, _options);

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
                _operations = operations;
                _recordSets = recordSets;
                _state = LatticeDatabaseStateEnum.Ready;
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

    // A publication failed (WriteCoordinator, "A fault"): the instance is Faulted unless a close came
    // first. The reads are marked first, so that a read that retries on the mark of the write that
    // failed gives up at once, then the state, then the waits for a position end.
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
