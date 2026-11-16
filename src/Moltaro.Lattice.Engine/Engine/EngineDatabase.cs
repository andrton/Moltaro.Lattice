using System.Collections.Concurrent;
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
// contract runs on once the placeholder of the core library is gone. In S06 it is a MemoryOnly
// master that inserts, reads, replaces, upserts and deletes entities of scalar fields by primary
// key, by class and by name (brief of Epic 1, sprint S06).
//
// How a test reaches it. Until S12 LatticeDatabase still opens the placeholder, and a test opens
// the engine through Create, the internal entry of exception X4 (docs/work/exceptions.md): only
// the opening differs, and every operation after it goes through the public interfaces. In S12
// LatticeDatabase returns this class, and Create is removed with its uses.
//
// Create. In S06 an instance is a MemoryOnly master. Create makes it without opening it and
// refuses in the order of LatticeDatabase.Create: ArgumentNullException for null options;
// Validate, which gives InvalidConfiguration; then NotSupportedException for Durable persistence
// (until S27), for a child role (until S10) and for a ListenUri (until S10).
//
// States. Created, Loading during OpenAsync, Ready, Closed; Connecting and CatchingUp do not
// occur, and Faulted only through a failed publication, which S09 tests. OpenAsync compiles the
// root layout of every entity type before any data is admitted: a fixed part larger than one
// block fails the open with InvalidSchema, and an entity type with a field the engine cannot
// store yet gets no layout (the planner's answer to point 1 of Q35). A failed open closes the
// instance. Before the open an operation is Unavailable; after a close it is
// ObjectDisposedException. A close frees the arena once nothing can reach it any more: it marks the
// reads closed, so that a read that enters afterwards leaves at once (ReadEpochs), lets no write, drain
// or count of the counters start (CloseBarrier), waits for those in progress and then for the reads
// that entered before the mark, and only then frees the arena with everything retired. It does not
// wait for that inside the call: CloseAsync returns a task that completes then, and a cancellation of
// its token ends only the wait of the caller. A wait for a position ends with the close.
//
// What the parts do, and where. The entity types and their reads are in Entities (EntityTable, one
// for each entity type, with its root layout), the primary indexes in Indexes (PrimaryIndex), the
// writes in Writes (WriteExecutor), the protection of reads by epochs, what is retired and the
// barrier of the close in Protection, the history and the sequence in Positions (PositionClock), and
// the sets of the public interfaces in Sets.
//
// The order of the locks and of the lifetimes. The database has two locks: the lock of the state,
// for an open and a close that race, and the lock of the writes (WriteExecutor), under which one
// write, one drain or one count of the counters runs at a time and which is held for synchronous
// work in memory only. A read takes neither: it enters the protection of reads, which is no lock and
// never waits for the writer, and nothing is taken inside it. A thread that holds the lock of the
// writes takes no other lock. What a read can reach lives at least as long as the read: a write
// unlinks it and retires it, and the writer frees it only once the reads that entered before have
// left (RetiredMemory); the close frees the arena only after the reads and the writes in progress
// have ended. No code of the application runs, and nothing waits, while a read is inside.
//
// Position and revision. Each open has a new random non-zero history; the sequence is 0 and grows
// by 1 with each published write, also one that changed nothing (the owner's answer to Q39). A
// revision is the history and the root entry's LastMutationSequence: in S06 every position
// changes at most one entity, so the revision of one entity never satisfies a condition on another,
// and a recreated entity has a new one. LatticeRevision is two opaque 64-bit values, so this
// composition is internal and can change; how a revision binds its entity when several entities
// change at one position belongs to the stage with the batch (milestone contract, "Write
// coordinator and revision semantics"; D4 of Epic 1).
//
// The limit before S09. The arena gets half of GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
// read at open, as an internal value that S09 turns into an option; a write the arena cannot hold
// is WriteRejected, without a Limit until S09. An entity has no size limit of its own (decision
// O19 of Epic 1); one String or Binary value above 268,173,376 bytes, the capacity of one directory
// block of chunks, is ValidationFailed with the field named, a fixed rule of the storage format,
// which the validation of the core checks before a write reaches the engine.
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

    // The options of the arena and the allocator of its native memory that a test of the engine gave
    // (the second Create); both null for every other instance, whose arena gets the limit before S09
    // and the native allocator itself.
    private readonly ArenaOptions? _arenaOptions;

    private readonly NativeAllocator? _allocator;

    // The sets of classes, made once for each class and shared.
    private readonly ConcurrentDictionary<Type, object> _classSets = new();

    private volatile LatticeDatabaseStateEnum _state = LatticeDatabaseStateEnum.Created;

    // The parts of the open database. Set by the open before the state becomes Ready, which a member
    // reads first, and never changed afterwards.
    private LatticeSchema? _schema;

    private Arena? _arena;

    private RootTable? _roots;

    private RetiredMemory? _retired;

    private PositionClock? _clock;

    // The lock of the writes, which the counters and the drain take as well (WriteExecutor).
    private Lock? _writes;

    private Dictionary<LatticeEntityType, SetOperations>? _operations;

    private Dictionary<LatticeEntityType, RecordSet>? _recordSets;

    private EngineDatabase(LatticeDatabaseOptions options, ArenaOptions? arenaOptions, NativeAllocator? allocator)
    {
        _options = options;
        _arenaOptions = arenaOptions;
        _allocator = allocator;
        _barrier = new CloseBarrier(this);
        _epochs = new ReadEpochs(this);
    }

    // Makes an instance for the options without opening it. In S06 a MemoryOnly master.
    public static EngineDatabase Create(LatticeDatabaseOptions options)
    {
        Check(options);

        return new EngineDatabase(options, arenaOptions: null, allocator: null);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): makes an instance as the
    // first Create does, with the same refusals, whose arena takes these options and obtains and
    // returns its native memory through this allocator, instead of the limit before S09 and the native
    // allocator itself. So a test can bound the arena to a few segments and count every native byte
    // the database obtained and returned (Storage.Allocation.NativeAllocator). Nothing else differs,
    // and an instance made by the first Create pays nothing for it.
    public static EngineDatabase Create(LatticeDatabaseOptions options, ArenaOptions arenaOptions, NativeAllocator allocator)
    {
        Check(options);
        ArgumentNullException.ThrowIfNull(arenaOptions);
        ArgumentNullException.ThrowIfNull(allocator);

        return new EngineDatabase(options, arenaOptions, allocator);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): makes an instance as the
    // first Create does, with the same refusals, whose primary indexes compute every hash with this
    // seed instead of one the instance draws when it opens (the planner's answer to Q50; brief of
    // S08, part 2), so that a test can build keys that share one hash under one seed and show them
    // spread under another, and find every key under two seeds it chose. Nothing else differs, and
    // an instance made by the first Create pays nothing for it. The stub of S08, part 2: until its
    // implementer gives the indexes a seed, they take none and this one is ignored.
    public static EngineDatabase Create(LatticeDatabaseOptions options, ulong indexSeed)
    {
        Check(options);

        return new EngineDatabase(options, arenaOptions: null, allocator: null);
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
    // two writes, under the lock of the writes, so that no write and no freeing of what was retired
    // is half done.
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

                    return new EngineCounters(_arena!.Counters, indexBytes, _retired!.IndexBytes, replacementBytes, _roots!.Count, (long)_clock!.Sequence);
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
    // drain, part 1"). It is what a write under memory pressure calls, what a close calls after it
    // has waited for the reads, and what a test calls before it counts. Unavailable before the open,
    // ObjectDisposedException after the close. It runs under the lock of the writes, between two
    // writes, because only that thread frees (RetiredMemory).
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
    // counters meanwhile, since the lock of the writes is held. When hold returns the growth goes on as it would have. A
    // growth when no test has armed the switch pays one load of a field that is null, and a read
    // pays nothing. The entity type has to be one the engine stores. Unavailable before the open,
    // ObjectDisposedException after the close.
    public void HoldNextGrowth(LatticeEntityType entityType, Action hold)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(hold);
        ThrowIfNotReady();

        _operations![entityType].Table.HoldNextGrowth(hold);
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next write that the arena
    // refused and that waits for the reads that hold retired memory runs hold, once, on its own thread
    // and inside the lock of the writes, when it knows it has to wait and before it waits, so that a
    // test can tell that the write waits instead of giving it a window of time. When hold returns the
    // write waits as it would have. Only a write the arena refused loads the switch: no other write and
    // no read pays anything for it. Unavailable before the open, ObjectDisposedException after the
    // close.
    public void HoldNextWaitForReads(Action hold)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ThrowIfNotReady();

        _retired!.HoldNextWait(hold);
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
    // reads and writes in progress have ended (see the class). The cancellation token ends only the
    // wait for that: the database still closes, and the arena is freed when they have ended.
    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_state == LatticeDatabaseStateEnum.Closed)
            {
                return ValueTask.CompletedTask;
            }

            _state = LatticeDatabaseStateEnum.Closed;
        }

        _clock?.Close();

        // The mark first: a read that enters from now on leaves at once, and the reads that entered
        // before announce an epoch below the one returned here.
        var mark = _epochs.Close();
        var closed = FreeAfterOperationsAsync(_barrier.Close(), mark);

        return closed.IsCompleted ? new ValueTask(closed) : new ValueTask(closed.WaitAsync(cancellationToken));
    }

    public ValueTask DisposeAsync() => CloseAsync();

    public void Dispose() => CloseAsync().AsTask().GetAwaiter().GetResult();

    // Frees the arena, and with it every block, the retired ones included, once the writes, drains and
    // counts of the counters in progress have ended and then the reads that entered before the mark of
    // the close. When nothing is in progress it does so before it returns. The wait for the reads
    // runs on a thread of the pool, because a read may be held by a test while its caller goes on; it
    // ends as ReadEpochs.WaitForReadsBefore says.
    private async Task FreeAfterOperationsAsync(Task operationsEnded, long mark)
    {
        await operationsEnded.ConfigureAwait(false);

        if (_epochs.HasReadsBefore(mark))
        {
            await Task.Run(() => _epochs.WaitForReadsBefore(mark, CancellationToken.None)).ConfigureAwait(false);
        }

        // The parts are set only when the open published them; an open that the close overtook frees
        // its own arena (Load). The arena is the only native memory of the database; everything else
        // is managed.
        if (_arena is { } arena)
        {
            _retired!.Drain();
            _retired.Clear();
            arena.Dispose();
        }
    }

    // Builds the parts of the open database and makes it Ready. A close that came while it loaded
    // wins: what was built is freed, and the open fails with ObjectDisposedException.
    private void Load()
    {
        var schema = _options.Schema!;

        // The memory of the process, as .NET reports it: half of it for the arena (brief of S06,
        // "The limit before S09"; the owner's answer to Q37).
        var arena = _allocator is null
            ? new Arena(new ArenaOptions { MemoryLimit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2 })
            : new Arena(_arenaOptions!, _allocator);

        try
        {
            var roots = new RootTable();
            var retired = new RetiredMemory(_epochs, arena, roots);

            // A new history at every open: nothing of a MemoryOnly database survives it, so no
            // position of an earlier run may be taken for one of this run.
            var clock = new PositionClock((ulong)Random.Shared.NextInt64(1, long.MaxValue), this);
            var writes = new Lock();
            var executor = new WriteExecutor(writes, arena, roots, retired, clock, _barrier, _options.SynchronizationTimeout);
            var operations = new Dictionary<LatticeEntityType, SetOperations>();
            var recordSets = new Dictionary<LatticeEntityType, RecordSet>();

            foreach (var entityType in schema.EntityTypes)
            {
                var table = EntityTable.Open(entityType, schema, arena, roots, _epochs, retired, clock);
                var forType = new SetOperations(table, executor, clock, _options.SynchronizationTimeout);

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
