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
// ObjectDisposedException. A close waits for the reads in progress before it frees the arena
// (CloseBarrier): every read and write enters the barrier before it touches the database, and the
// arena is freed by the last one to leave after the close, or by the close when none is in
// progress. A wait for a position ends with the close.
//
// What the parts do, and where. The entity types and their reads are in Entities (EntityTable, one
// for each entity type, with its root layout and its primary index), the writes in Writes
// (WriteExecutor), the gates, the lookup claims and the barrier of the close in Protection (with
// the order of the locks, RootGates), the history and the sequence in Positions (PositionClock), and
// the sets of the public interfaces in Sets.
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

    // The sets of classes, made once for each class and shared.
    private readonly ConcurrentDictionary<Type, object> _classSets = new();

    private volatile LatticeDatabaseStateEnum _state = LatticeDatabaseStateEnum.Created;

    // The parts of the open database. Set by the open before the state becomes Ready, which a member
    // reads first, and never changed afterwards.
    private LatticeSchema? _schema;

    private Arena? _arena;

    private RootGates? _gates;

    private LookupClaims? _claims;

    private PositionClock? _clock;

    // The lock of the writes, which the counters take as well (WriteExecutor).
    private Lock? _writes;

    private Dictionary<LatticeEntityType, SetOperations>? _operations;

    private Dictionary<LatticeEntityType, RecordSet>? _recordSets;

    private EngineDatabase(LatticeDatabaseOptions options)
    {
        _options = options;
        _barrier = new CloseBarrier(this);
    }

    // Makes an instance for the options without opening it. In S06 a MemoryOnly master.
    public static EngineDatabase Create(LatticeDatabaseOptions options)
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

        return new EngineDatabase(options);
    }

    // What the engine holds at this moment, for the tests and later for diagnostics: taken between
    // two writes, under the lock of the writes, and under the catalog gate, so that no write and no
    // release of an entry by a lookup is half done.
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
                    _gates!.Catalog.EnterReadLock();

                    try
                    {
                        var indexBytes = 0L;

                        foreach (var operations in _operations!.Values)
                        {
                            indexBytes += operations.Table.IndexBytes;
                        }

                        // Nothing is retired and the index grows in place until S08 part 1, so the
                        // retired and the replacement tables of the index hold nothing.
                        return new EngineCounters(_arena!.Counters, indexBytes, 0, 0, _claims!.Table.Count, (long)_clock!.Sequence);
                    }
                    finally
                    {
                        _gates.Catalog.ExitReadLock();
                    }
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
    // ObjectDisposedException after the close.
    //
    // Until the protection of reads of S08 part 1 is built nothing is ever retired: a write frees what
    // it replaces or removes under the root's exclusive protection, and the entry of a deleted root
    // is released by the delete or by the last lookup that drops its claim. So the drain has nothing
    // to free, and its body is the check of the state; the implementer of S08 part 1 replaces it.
    public void DrainRetired()
    {
        ThrowIfNotReady();
    }

    // For the tests of the engine (exception X3, docs/work/exceptions.md): the next read by key that
    // finds its root in the index runs hold, once, on its own thread and inside the protection of
    // reads, after it has found the root and before it copies it, with the handle of the root entry
    // it found. A test waits inside hold, with a bounded wait, to keep that read inside the
    // protection while the test writes, drains, counts and closes; when hold returns, the read goes
    // on as it would have. Every other read passes the point without running anything. The switch
    // can be armed again while a read it held earlier is still held, so that two reads are held at
    // once. A read when no test has armed the switch pays nothing for it beyond reading one field
    // that is null; how it is kept that cheap is the implementer's.
    //
    // NotSupportedException until the protection of reads of S08 part 1 is built: under the gates of
    // S06 a read held inside the protection of its root holds the root's stripe, and every write of
    // that root waits for it, which is what part 1 ends.
    public void HoldNextRead(Action<RootHandle> hold)
    {
        ArgumentNullException.ThrowIfNull(hold);

        throw new NotSupportedException("A read cannot be held inside the protection until reads are protected by epochs (Epic 1, sprint S08, part 1).");
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
    // reads and writes in progress have ended. The cancellation token ends only the wait for that:
    // the database still closes, and the last operation to end frees the arena.
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

        // The arena is the only native memory of the database; everything else is managed.
        var drained = _barrier.Close(() => _arena?.Dispose());

        return drained.IsCompleted ? ValueTask.CompletedTask : new ValueTask(drained.WaitAsync(cancellationToken));
    }

    public ValueTask DisposeAsync() => CloseAsync();

    public void Dispose() => CloseAsync().AsTask().GetAwaiter().GetResult();

    // Builds the parts of the open database and makes it Ready. A close that came while it loaded
    // wins: what was built is freed, and the open fails with ObjectDisposedException.
    private void Load()
    {
        var schema = _options.Schema!;

        // The memory of the process, as .NET reports it: half of it for the arena (brief of S06,
        // "The limit before S09"; the owner's answer to Q37).
        var arena = new Arena(new ArenaOptions { MemoryLimit = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2 });

        try
        {
            var gates = new RootGates();
            var claims = new LookupClaims(new RootTable(), gates);

            // A new history at every open: nothing of a MemoryOnly database survives it, so no
            // position of an earlier run may be taken for one of this run.
            var clock = new PositionClock((ulong)Random.Shared.NextInt64(1, long.MaxValue), this);
            var writes = new Lock();
            var executor = new WriteExecutor(writes, arena, gates, claims, clock, _barrier, _options.SynchronizationTimeout);
            var operations = new Dictionary<LatticeEntityType, SetOperations>();
            var recordSets = new Dictionary<LatticeEntityType, RecordSet>();

            foreach (var entityType in schema.EntityTypes)
            {
                var table = EntityTable.Open(entityType, schema, arena, gates, claims, clock, _barrier);
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
                _gates = gates;
                _claims = claims;
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
