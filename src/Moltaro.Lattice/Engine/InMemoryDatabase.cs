using System.Collections.Concurrent;
using System.Threading.Channels;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine;

// One database instance: its lifecycle and role, and how reads and writes are routed.
// A master executes writes here and passes them to its children; a child forwards writes
// to its master and applies what the master confirms, in the master's order.
//
// Every write gets the next position of the database history. A master's position is the
// last write it has published; a child's position is the last write it has applied. An
// operation that depends on a position waits, outside the gate, until this node has
// reached it.
//
// Placeholder for the native engine: managed data, MemoryOnly, replication inside one
// process. It follows the contract's semantics, not the engine's layout or performance.
//
// Lock order: master gate -> child gate. A child never takes its master's gate while
// holding its own.
internal sealed class InMemoryDatabase : ILatticeDatabase, IEntityOperations, IReplicationMaster, IReplicationChild
{
    // Operations that may wait for a position at the same time. Beyond it they are refused.
    private const int MaximumWaiters = 4096;

    private readonly Lock _gate = new();
    private readonly LatticeDatabaseOptions _options;
    private readonly List<IReplicationChild> _children = [];
    private readonly List<PositionWaiter> _waiters = [];
    private readonly ConcurrentDictionary<Type, object> _typedSets = new();
    private readonly CancellationTokenSource _closing = new();
    private LatticeSchema? _schema;
    private EntityStore? _store;
    private IReplicationMaster? _master;
    private Channel<ReplicatedChange>? _inbox;
    private TaskCompletionSource? _applyPause;
    private ulong _history;
    private ulong _sequence;
    private volatile LatticeDatabaseStateEnum _state = LatticeDatabaseStateEnum.Created;

    public InMemoryDatabase(LatticeDatabaseOptions options)
    {
        _options = options;
    }

    public LatticeDatabaseStateEnum State => _state;

    public LatticeSchema Schema
    {
        get
        {
            lock (_gate)
            {
                ThrowIfNotReady();
                return _schema!;
            }
        }
    }

    public LatticePosition Position
    {
        get
        {
            lock (_gate)
            {
                ThrowIfNotReady();
                return new LatticePosition(_history, _sequence);
            }
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

        return AwaitAppliedAsync(position, timeout ?? _options.SynchronizationTimeout, cancellationToken);
    }

    public ValueTask WaitForPositionAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        // A session without a position has nothing to wait for, but the answer "you can
        // read now" still requires a database that is ready.
        _ = Position;
        return ((IEntityOperations)this).SynchronizeAsync(session, cancellationToken);
    }

    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LatticeDatabaseStateEnum.Closed, this);
            if (_state != LatticeDatabaseStateEnum.Created)
            {
                throw new InvalidOperationException("The database has already been opened.");
            }

            _state = _options.Role == LatticeRoleEnum.Master ? LatticeDatabaseStateEnum.Loading : LatticeDatabaseStateEnum.Connecting;
        }

        try
        {
            if (_options.Role == LatticeRoleEnum.Master)
            {
                StartMaster();
            }
            else
            {
                // Closing the database also ends a pending connection attempt.
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token);
                await ConnectAsync(cancellation.Token);
            }
        }
        catch
        {
            await CloseAsync(CancellationToken.None);
            throw;
        }
    }

    public ILatticeSet<TEntity> Set<TEntity>()
        where TEntity : class
    {
        var schema = Schema;
        return (ILatticeSet<TEntity>)_typedSets.GetOrAdd(
            typeof(TEntity),
            static (_, state) => new TypedSet<TEntity>(state.database, EntityBinding<TEntity>.For(state.schema)),
            (database: this, schema));
    }

    public ILatticeSet Set(string entityName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityName);

        return Schema.TryGetEntityType(entityName, out var entityType)
            ? new DynamicSet(this, entityType)
            : throw new LatticeException(LatticeErrorEnum.BindingFailed, $"The schema has no entity type '{entityName}'.");
    }

    public ILatticeWriteBatch CreateWriteBatch() => new WriteBatch(Schema);

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        _closing.Cancel();

        if (_options.ListenUri is { } address)
        {
            InProcessEndpoints.Unregister(address, this);
        }

        IReplicationMaster? master;
        IReplicationChild[] children;
        PositionWaiter[] waiters;
        lock (_gate)
        {
            if (_state == LatticeDatabaseStateEnum.Closed)
            {
                return ValueTask.CompletedTask;
            }

            _state = LatticeDatabaseStateEnum.Closed;
            master = _master;
            _master = null;
            children = [.. _children];
            _children.Clear();
            waiters = [.. _waiters];
            _waiters.Clear();
            _store = null;
            _inbox?.Writer.TryComplete();
        }

        master?.Detach(this);
        foreach (var child in children)
        {
            child.MasterClosed(this);
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(
                new LatticeException(LatticeErrorEnum.Unavailable, "The database was closed while the operation was waiting for a position."));
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => CloseAsync();

    public void Dispose() => CloseAsync().AsTask().GetAwaiter().GetResult();

    // Test seam: holds back the changes a child has received, as a slow link would. The
    // child keeps receiving; it applies again when the returned object is disposed.
    internal IDisposable SuspendApplying()
    {
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _applyPause, pause);
        return new ApplyPause(this, pause);
    }

    // --- Reads and writes of the sets ---

    bool IEntityOperations.TryRead(LatticeEntityType entityType, object key, out StoredEntity stored, out LatticePosition position)
    {
        lock (_gate)
        {
            ThrowIfNotReady();
            position = new LatticePosition(_history, _sequence);
            return _store!.TryGet(entityType.Name, key, out stored);
        }
    }

    StoredEntity[] IEntityOperations.ReadAll(LatticeEntityType entityType, out LatticePosition position)
    {
        lock (_gate)
        {
            ThrowIfNotReady();
            position = new LatticePosition(_history, _sequence);
            return _store!.GetAll(entityType.Name);
        }
    }

    ValueTask IEntityOperations.SynchronizeAsync(LatticeSession session, CancellationToken cancellationToken) =>
        session.MinPosition is { } minPosition
            ? AwaitAppliedAsync(minPosition, session.SynchronizationTimeout ?? _options.SynchronizationTimeout, cancellationToken)
            : cancellationToken.IsCancellationRequested ? ValueTask.FromCanceled(cancellationToken) : ValueTask.CompletedTask;

    // The caller does not know the role: a master executes the write here, a child sends
    // it to its master and then waits until it has applied the write itself.
    async ValueTask<LatticeWriteResult> IEntityOperations.WriteAsync(WriteKindEnum kind, LatticeEntityType entityType, object key, object?[]? values, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken)
    {
        EnsureWritable(entityType);

        cancellationToken.ThrowIfCancellationRequested();

        var timeout = session?.SynchronizationTimeout ?? _options.SynchronizationTimeout;
        var request = new WriteRequest(kind, entityType.Name, key, values, expectedRevision, session?.MinPosition, timeout);

        IReplicationMaster master;
        lock (_gate)
        {
            ThrowIfNotReady();
            master = _options.Role == LatticeRoleEnum.Master
                ? this
                : _master ?? throw new LatticeException(LatticeErrorEnum.Unavailable, "The child is not connected to its master; the write was not executed.");
        }

        var outcome = await master.ExecuteAsync(request, cancellationToken);

        if (!ReferenceEquals(master, this))
        {
            // The write is committed. From here on nothing cancels or undoes it; the only
            // open question is when this child has applied it.
            try
            {
                await AwaitAppliedAsync(outcome.Position, timeout, CancellationToken.None);
            }
            catch (LatticeException exception)
            {
                session?.Advance(outcome.Position);
                throw new LatticeException(
                    exception.Error,
                    $"The write is committed by the master at position {outcome.Position}, but this child has not applied it yet. It is not rolled back; do not submit it again. An operation that requires this position will see it.",
                    exception)
                {
                    CommittedPosition = outcome.Position,
                };
            }
        }

        session?.Advance(outcome.Position);
        return new LatticeWriteResult(outcome.Position, outcome.Changed);
    }

    // The placeholder keeps values as .NET objects, compares them as .NET does by default and
    // maintains no index. An entity type that declares what it cannot honor is not written at
    // all, rather than accepted under other rules than the declared ones: a unique index; a
    // comparison that ignores case and Dictionary storage, which it would not apply; a field
    // of an enum type, whose values it would not check; any entity type of a schema that
    // declares the UTF-16 encoding.
    private void EnsureWritable(LatticeEntityType entityType)
    {
        foreach (var index in entityType.Indexes)
        {
            if (index.IsUnique)
            {
                throw new NotSupportedException(
                    $"Unique indexes are not implemented yet: '{entityType.Name}' declares the unique index '{index.Name}', so it cannot be written.");
            }
        }

        foreach (var field in entityType.Fields)
        {
            var declaration = field.Declaration;
            if (declaration.Comparison == LatticeStringComparisonEnum.OrdinalIgnoreCase)
            {
                throw new NotSupportedException(
                    $"Comparisons that ignore case are not implemented yet: '{entityType.Name}.{field.Name}' declares OrdinalIgnoreCase, so '{entityType.Name}' cannot be written.");
            }

            if (declaration.StringStorage == LatticeStringStorageEnum.Dictionary)
            {
                throw new NotSupportedException(
                    $"Dictionary string storage is not implemented yet: '{entityType.Name}.{field.Name}' declares it, so '{entityType.Name}' cannot be written.");
            }

            if (declaration.Type == LatticeFieldTypeEnum.Enum)
            {
                throw new NotSupportedException(
                    $"Enum fields are not implemented yet: '{entityType.Name}.{field.Name}' is of enum type '{declaration.EnumType!.Name}', so '{entityType.Name}' cannot be written.");
            }
        }

        // _schema is set before the database becomes Ready and never changes afterwards, and a
        // set, through which every write comes, is created only from a Ready database.
        if (_schema!.StringEncoding != LatticeStringEncodingEnum.Utf8)
        {
            throw new NotSupportedException(
                $"The {_schema.StringEncoding} string encoding is not implemented yet: the schema declares it, so '{entityType.Name}' cannot be written.");
        }
    }

    // --- Positions ---

    // Completes when this node has applied the position. The wait happens outside the gate
    // and always ends: by reaching the position, by the timeout or by cancellation.
    private ValueTask AwaitAppliedAsync(LatticePosition position, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        PositionWaiter waiter;
        lock (_gate)
        {
            ThrowIfNotReady();

            if (position.History != _history)
            {
                throw new LatticeException(
                    LatticeErrorEnum.HistoryMismatch,
                    "The required position belongs to another database history than this database. Start a new session.");
            }

            if (position.Sequence <= _sequence)
            {
                return ValueTask.CompletedTask;
            }

            if (_waiters.Count >= MaximumWaiters)
            {
                throw new LatticeException(LatticeErrorEnum.Unavailable, "Too many operations are waiting for this node to reach a position.");
            }

            waiter = new PositionWaiter(position.Sequence, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            _waiters.Add(waiter);
        }

        return new ValueTask(AwaitPositionAsync(waiter, position, timeout, cancellationToken));
    }

    private async Task AwaitPositionAsync(PositionWaiter waiter, LatticePosition position, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await waiter.Completion.Task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            Forget(waiter);
            throw new LatticeException(
                LatticeErrorEnum.SynchronizationTimeout,
                $"This node did not reach position {position} within {timeout}.");
        }
        catch (OperationCanceledException)
        {
            Forget(waiter);
            throw;
        }
    }

    private void Forget(PositionWaiter waiter)
    {
        lock (_gate)
        {
            _waiters.Remove(waiter);
        }
    }

    // Call inside the gate after the position has moved; complete the result outside it.
    private List<PositionWaiter>? TakeReachedWaiters()
    {
        List<PositionWaiter>? reached = null;
        for (var index = _waiters.Count - 1; index >= 0; index--)
        {
            if (_waiters[index].Sequence <= _sequence)
            {
                (reached ??= []).Add(_waiters[index]);
                _waiters.RemoveAt(index);
            }
        }

        return reached;
    }

    private static void Complete(List<PositionWaiter>? reached)
    {
        if (reached is null)
        {
            return;
        }

        foreach (var waiter in reached)
        {
            waiter.Completion.TrySetResult();
        }
    }

    // --- Master side ---

    private void StartMaster()
    {
        var schema = _options.Schema!;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == LatticeDatabaseStateEnum.Closed, this);
            _schema = schema;
            _store = new EntityStore(schema);

            // A new history every time: nothing of a MemoryOnly database survives, so no
            // position of an earlier run may ever be taken for a position of this one.
            _history = unchecked((ulong)Random.Shared.NextInt64(1, long.MaxValue));
            _sequence = 0;
            _state = LatticeDatabaseStateEnum.Ready;
        }

        if (_options.ListenUri is { } address)
        {
            try
            {
                InProcessEndpoints.Register(address, this);
            }
            catch
            {
                _state = LatticeDatabaseStateEnum.Closed;
                throw;
            }
        }
    }

    bool IReplicationMaster.TryAttach(IReplicationChild child)
    {
        lock (_gate)
        {
            if (_state != LatticeDatabaseStateEnum.Ready
                || !child.TryInstall(this, _schema!, _store!.Clone(), new LatticePosition(_history, _sequence)))
            {
                return false;
            }

            _children.Add(child);
            return true;
        }
    }

    void IReplicationMaster.Detach(IReplicationChild child)
    {
        lock (_gate)
        {
            _children.Remove(child);
        }
    }

    // Orders the write after the position it depends on, checks its condition against the
    // current state, applies it and hands it to the children. Does not wait for them.
    async ValueTask<WriteOutcome> IReplicationMaster.ExecuteAsync(WriteRequest request, CancellationToken cancellationToken)
    {
        if (_state != LatticeDatabaseStateEnum.Ready)
        {
            throw MasterUnavailable();
        }

        if (request.MinPosition is { } minPosition)
        {
            await AwaitAppliedAsync(minPosition, request.SynchronizationTimeout, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        WriteOutcome outcome;
        List<PositionWaiter>? reached;
        lock (_gate)
        {
            if (_state != LatticeDatabaseStateEnum.Ready)
            {
                throw MasterUnavailable();
            }

            var store = _store!;
            var exists = store.TryGet(request.EntityName, request.Key, out var current);

            if (request.ExpectedRevision is { } expected)
            {
                if (!exists || current.Revision != expected)
                {
                    throw new LatticeException(LatticeErrorEnum.RevisionConflict, $"{request.EntityName} '{request.Key}' has another revision or no longer exists.");
                }
            }
            else if (request.Kind == WriteKindEnum.Insert && exists)
            {
                throw new LatticeException(LatticeErrorEnum.DuplicateKey, $"{request.EntityName} '{request.Key}' already exists.");
            }
            else if (request.Kind == WriteKindEnum.Replace && !exists)
            {
                throw new LatticeException(LatticeErrorEnum.NotFound, $"{request.EntityName} '{request.Key}' does not exist.");
            }
            else if (request.Kind == WriteKindEnum.Delete && !exists)
            {
                // Nothing to do, and still an answer that is true as of this position.
                return new WriteOutcome(false, new LatticePosition(_history, _sequence));
            }

            var sequence = _sequence + 1;
            StoredEntity? stored = request.Kind == WriteKindEnum.Delete
                ? null
                : new StoredEntity(request.Values!, new LatticeRevision(_history, sequence));

            store.Apply(request.EntityName, request.Key, stored);
            _sequence = sequence;

            var change = new ReplicatedChange(request.EntityName, request.Key, stored, sequence);
            foreach (var child in _children)
            {
                child.Receive(change);
            }

            reached = TakeReachedWaiters();
            outcome = new WriteOutcome(true, new LatticePosition(_history, sequence));
        }

        Complete(reached);
        return outcome;
    }

    private static LatticeException MasterUnavailable() =>
        new(LatticeErrorEnum.Unavailable, "The master is not available; the write was not executed.");

    // --- Child side ---

    private async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var master = await InProcessEndpoints.ConnectAsync(_options.MasterUri!, cancellationToken);
            if (master.TryAttach(this))
            {
                return;
            }
        }
    }

    bool IReplicationChild.TryInstall(IReplicationMaster master, LatticeSchema schema, EntityStore initialState, LatticePosition position)
    {
        Channel<ReplicatedChange> inbox;
        lock (_gate)
        {
            // The child was closed while it was connecting.
            if (_state != LatticeDatabaseStateEnum.Connecting)
            {
                return false;
            }

            inbox = Channel.CreateUnbounded<ReplicatedChange>(new UnboundedChannelOptions { SingleReader = true });
            _inbox = inbox;
            _master = master;
            _schema = schema;
            _store = initialState;
            _history = position.History;
            _sequence = position.Sequence;
            _state = LatticeDatabaseStateEnum.Ready;
        }

        _ = Task.Run(() => ApplyReceivedAsync(inbox.Reader));
        return true;
    }

    void IReplicationChild.Receive(ReplicatedChange change) => _inbox!.Writer.TryWrite(change);

    void IReplicationChild.MasterClosed(IReplicationMaster master)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_master, master))
            {
                _master = null;

                // What has been received is still applied; nothing more will come.
                _inbox?.Writer.TryComplete();
            }
        }
    }

    // Applies the received changes one by one, in the master's order, and lets the
    // operations go that were waiting for the position each change brings.
    private async Task ApplyReceivedAsync(ChannelReader<ReplicatedChange> inbox)
    {
        try
        {
            await foreach (var change in inbox.ReadAllAsync())
            {
                if (Volatile.Read(ref _applyPause) is { } pause)
                {
                    await pause.Task;
                }

                List<PositionWaiter>? reached;
                lock (_gate)
                {
                    if (_state != LatticeDatabaseStateEnum.Ready)
                    {
                        return;
                    }

                    _store!.Apply(change.EntityName, change.Key, change.Stored);
                    _sequence = change.Sequence;
                    reached = TakeReachedWaiters();
                }

                Complete(reached);
            }
        }
        catch (Exception exception)
        {
            // A change that cannot be applied leaves this child without a coherent state.
            Fault(exception);
        }
    }

    private void Fault(Exception exception)
    {
        PositionWaiter[] waiters;
        lock (_gate)
        {
            if (_state != LatticeDatabaseStateEnum.Ready)
            {
                return;
            }

            _state = LatticeDatabaseStateEnum.Faulted;
            waiters = [.. _waiters];
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(
                new LatticeException(LatticeErrorEnum.Unavailable, "The database has faulted while applying a change from its master.", exception));
        }
    }

    private void ThrowIfNotReady()
    {
        ObjectDisposedException.ThrowIf(_state == LatticeDatabaseStateEnum.Closed, this);
        if (_state != LatticeDatabaseStateEnum.Ready)
        {
            throw new LatticeException(LatticeErrorEnum.Unavailable, $"The database is {_state}, not Ready.");
        }
    }

    // An operation waiting until this node has applied the transaction with this number.
    private sealed record PositionWaiter(ulong Sequence, TaskCompletionSource Completion);

    private sealed class ApplyPause(InMemoryDatabase database, TaskCompletionSource pause) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.CompareExchange(ref database._applyPause, null, pause);
            pause.TrySetResult();
        }
    }
}
