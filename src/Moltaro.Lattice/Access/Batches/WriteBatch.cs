using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Batches;

// A write batch under construction: the operations in the order they were added, each
// already checked against the schema, and its entity encoded by the engine to the root it
// writes (IWriteBatchExecutor), so that later edits of the caller's object are not part of the
// batch. The encoding is the caller's work, on its thread, outside every lock of the database.
//
// The commit hands the operations to the database (IWriteBatchExecutor.ExecuteAsync) and lets go of them,
// so that a batch kept after its commit keeps no root alive.
internal sealed class WriteBatch : ILatticeWriteBatch
{
    private readonly LatticeSchema _schema;

    private readonly IWriteBatchExecutor _executor;
    private List<WriteBatchOperation> _operations = [];

    // The entities the batch already addresses, told apart as the schema compares their keys
    // (WriteBatchEntity). A key is normalized to the key type of its entity type before it is
    // added, so an integer key is one entity in whatever width it was given.
    private readonly BudgetSet<WriteBatchEntity> _identities;
    private readonly MemoryBudget? _budget;
    private MemoryLease? _lease;
    private bool _submitted;
    private bool _hasUpdates;

    public WriteBatch(LatticeSchema schema, IWriteBatchExecutor executor, MemoryBudget? budget = null)
    {
        _schema = schema;
        _executor = executor;
        _budget = budget;
        _identities = new BudgetSet<WriteBatchEntity>(budget, nameof(WriteBatch), EqualityComparer<WriteBatchEntity>.Default);
        if (budget is not null)
        {
            var operations = _operations;
            var identities = _identities;
            _lease = new MemoryLease(budget);
            _lease.Own(() =>
            {
                budget.Release(BudgetCollections.Items(operations));
                operations.Clear();
                operations.Capacity = 0;
                identities.Dispose();
            });
        }
    }

    public int Count { get; private set; }

    public void Insert<TEntity>(TEntity entity)
        where TEntity : class =>
        Save(WriteKindEnum.Insert, entity, null);

    public void Insert(LatticeRecord record) => Save(WriteKindEnum.Insert, record, null);

    public void Replace<TEntity>(TEntity entity)
        where TEntity : class =>
        Save(WriteKindEnum.Replace, entity, null);

    public void Replace<TEntity>(TEntity entity, LatticeRevision expectedRevision)
        where TEntity : class =>
        Save(WriteKindEnum.Replace, entity, expectedRevision);

    public void Replace(LatticeRecord record) => Save(WriteKindEnum.Replace, record, null);

    public void Replace(LatticeRecord record, LatticeRevision expectedRevision) => Save(WriteKindEnum.Replace, record, expectedRevision);

    public void Upsert<TEntity>(TEntity entity)
        where TEntity : class =>
        Save(WriteKindEnum.Upsert, entity, null);

    public void Upsert(LatticeRecord record) => Save(WriteKindEnum.Upsert, record, null);

    public void Delete<TEntity>(object key)
        where TEntity : class =>
        Delete(EntityBinding<TEntity>.For(_schema).EntityType, key, null);

    public void Delete<TEntity>(object key, LatticeRevision expectedRevision)
        where TEntity : class =>
        Delete(EntityBinding<TEntity>.For(_schema).EntityType, key, expectedRevision);

    public void Delete<TKey>(string entityName, TKey key)
        where TKey : notnull =>
        Delete(Resolve(entityName), key, null);

    public void Delete<TKey>(string entityName, TKey key, LatticeRevision expectedRevision)
        where TKey : notnull =>
        Delete(Resolve(entityName), key, expectedRevision);

    public void Update(LatticeUpdate update) => AddUpdate(update, null);

    public void Update(LatticeUpdate update, LatticeRevision expectedRevision) => AddUpdate(update, expectedRevision);

    public ValueTask<LatticeWriteResult> CommitAsync(CancellationToken cancellationToken = default) =>
        Commit(null, cancellationToken);

    public ValueTask<LatticeWriteResult> CommitAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Commit(session, cancellationToken);
    }

    private void Save<TEntity>(WriteKindEnum kind, TEntity entity, LatticeRevision? expectedRevision)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        var binding = EntityBinding<TEntity>.For(_schema);
        using var snapshot = OpenSnapshot();
        try
        {
            Add(kind, binding.EntityType, binding.ValuesForWrite(entity, snapshot), expectedRevision, snapshot);
        }
        catch (BudgetRefusedException exception)
        {
            throw SnapshotRefusal(exception);
        }
    }

    private void Save(WriteKindEnum kind, LatticeRecord record, LatticeRevision? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_schema.TryGetEntityType(record.EntityType.Name, out var entityType) || !ReferenceEquals(entityType, record.EntityType))
        {
            throw new ArgumentException($"The record is a '{record.EntityType.Name}' of another schema, not of this database.", nameof(record));
        }

        using var snapshot = OpenSnapshot();
        try
        {
            Add(kind, entityType, record.ValuesForWrite(snapshot), expectedRevision, snapshot);
        }
        catch (BudgetRefusedException exception)
        {
            throw SnapshotRefusal(exception);
        }
    }

    private void Add(WriteKindEnum kind, LatticeEntityType entityType, object?[] values, LatticeRevision? expectedRevision, MemoryLease? snapshot) =>
        Add(kind, entityType, values[entityType.Key.Ordinal]!, values, expectedRevision, snapshot);

    private void Delete(LatticeEntityType entityType, object key, LatticeRevision? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(key);
        Add(WriteKindEnum.Delete, entityType, FieldValues.NormalizeKey(entityType, key), null, expectedRevision);
    }

    // Checked before anything is kept, and encoded before the operation is kept: a refused operation leaves
    // the batch as it was. The refusals come in this order: the validation of the entity by the core,
    // before Add (Save: a String or Binary value above its bound and every other check of the schema);
    // then a batch already submitted, InvalidOperationException; then an operation for an entity the
    // batch already addresses; then, as the engine encodes the root (IWriteBatchExecutor.EncodeRoot), a
    // database that was closed, ObjectDisposedException, and a List above its bound.
    private void Add(WriteKindEnum kind, LatticeEntityType entityType, object key, object?[]? values, LatticeRevision? expectedRevision,
        MemoryLease? snapshot = null)
    {
        var entity = CheckIdentity(entityType, key);
        using var addition = _budget is null ? null : new MemoryLease(_budget);
        try
        {
            var root = _executor.EncodeRoot(entityType, values, addition);
            PrepareCapacity();
            Keep(entity, new WriteBatchOperation(kind, entityType, key, expectedRevision, root));
            if (addition is not null)
            {
                snapshot?.Transfer(key, addition);
                _lease!.Take(addition);
            }
        }
        catch (BudgetRefusedException exception)
        {
            throw exception.WriteRejected();
        }
    }

    private void AddUpdate(LatticeUpdate update, LatticeRevision? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(update);
        update.ThrowIfSubmitted();
        if (!ReferenceEquals(update.Database, _executor))
        {
            throw new ArgumentException("The update belongs to another database instance.", nameof(update));
        }

        update.ThrowIfEmpty();
        // Finish any fallible allocation before changing either builder.
        using var addition = OpenSnapshot();
        try
        {
            var input = update.ValidateContent(addition);
            var entityType = update.EntityType;
            var key = update.Key;
            var entity = CheckIdentity(entityType, key);
            _executor.CheckUpdate(entityType, input);

            PrepareCapacity();
            Keep(entity, new WriteBatchOperation(WriteKindEnum.Update, entityType, key, expectedRevision, default, input));
            if (addition is not null)
            {
                _lease!.Take(addition);
            }

            _hasUpdates = true;
            update.Consume();
        }
        catch (BudgetRefusedException exception)
        {
            throw SnapshotRefusal(exception);
        }
    }

    private MemoryLease? OpenSnapshot() => _budget is not null && !_executor.IsClosed ? new MemoryLease(_budget) : null;

    private LatticeException SnapshotRefusal(BudgetRefusedException exception)
    {
        ObjectDisposedException.ThrowIf(_executor.IsClosed, _executor);
        return exception.WriteRejected();
    }

    private void PrepareCapacity()
    {
        var needed = Count + 1;
        if (_budget is null)
        {
            _operations.EnsureCapacity(needed);
            _identities.EnsureCapacity(needed);
            return;
        }

        if (needed <= _operations.Capacity)
        {
            _identities.EnsureCapacity(needed);
            return;
        }

        // Prepare both replacements before installing either list capacity or a new member.
        // The list's old array is still owned while the identity set is also growing.
        var previous = BudgetCollections.Items(_operations);
        var expanded = _budget.Allocate<WriteBatchOperation>(BudgetCollections.CapacityFor(previous.Length, needed), nameof(WriteBatch));
        try
        {
            previous.AsSpan(0, Count).CopyTo(expanded);
            _identities.EnsureCapacity(needed);
        }
        catch
        {
            _budget.Release(expanded);
            throw;
        }

        BudgetCollections.Items(_operations) = expanded;
        _budget.Release(previous);
    }

    private WriteBatchEntity CheckIdentity(LatticeEntityType entityType, object key)
    {
        ThrowIfSubmitted();
        var entity = new WriteBatchEntity(entityType, key);
        if (_identities.Contains(entity))
        {
            throw new ArgumentException(
                $"The batch already has an operation for {entityType.Name} '{key}'. A batch takes one operation per entity.");
        }

        return entity;
    }

    private void Keep(WriteBatchEntity entity, WriteBatchOperation operation)
    {
        _identities.Add(entity);
        _operations.Add(operation);
        Count++;
    }

    private LatticeEntityType Resolve(string entityName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityName);

        return _schema.TryGetEntityType(entityName, out var entityType)
            ? entityType
            : throw new LatticeException(LatticeErrorEnum.BindingFailed, $"The schema has no entity type '{entityName}'.");
    }

    // What the caller can know from its own arguments is thrown here; what the database decides comes
    // through the task.
    private ValueTask<LatticeWriteResult> Commit(LatticeSession? session, CancellationToken cancellationToken)
    {
        ThrowIfSubmitted();
        if (_operations.Count == 0)
        {
            throw new InvalidOperationException("The batch is empty; there is nothing to commit.");
        }

        _submitted = true;
        var operations = _operations;
        var lease = _lease;
        _lease = null;
        _operations = [];
        _identities.Dispose();

        try
        {
            var result = _executor.ExecuteAsync(operations, session, cancellationToken);
            if (_hasUpdates)
            {
                var completion = result.AsTask();
                foreach (var operation in operations)
                {
                    operation.Update?.Outcome.Attach(completion);
                }

                result = new ValueTask<LatticeWriteResult>(completion);
            }

            return lease is null ? result : ReleaseAfterAsync(result, lease);
        }
        catch
        {
            lease?.Dispose();
            throw;
        }
    }

    private static async ValueTask<LatticeWriteResult> ReleaseAfterAsync(ValueTask<LatticeWriteResult> result, MemoryLease lease)
    {
        using (lease)
        {
            return await result.ConfigureAwait(false);
        }
    }

    private void ThrowIfSubmitted()
    {
        if (_submitted)
        {
            throw new InvalidOperationException("The batch has already been submitted. Build a new batch.");
        }
    }
}
