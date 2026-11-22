using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Access.Values;
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
    private HashSet<WriteBatchEntity> _identities = [];
    private bool _submitted;

    public WriteBatch(LatticeSchema schema, IWriteBatchExecutor executor)
    {
        _schema = schema;
        _executor = executor;
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
        Add(kind, binding.EntityType, binding.ValuesForWrite(entity), expectedRevision);
    }

    private void Save(WriteKindEnum kind, LatticeRecord record, LatticeRevision? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_schema.TryGetEntityType(record.EntityType.Name, out var entityType) || !ReferenceEquals(entityType, record.EntityType))
        {
            throw new ArgumentException($"The record is a '{record.EntityType.Name}' of another schema, not of this database.", nameof(record));
        }

        Add(kind, entityType, record.ValuesForWrite(), expectedRevision);
    }

    private void Add(WriteKindEnum kind, LatticeEntityType entityType, object?[] values, LatticeRevision? expectedRevision) =>
        Add(kind, entityType, values[entityType.Key.Ordinal]!, values, expectedRevision);

    private void Delete(LatticeEntityType entityType, object key, LatticeRevision? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(key);
        Add(WriteKindEnum.Delete, entityType, FieldValues.NormalizeKey(entityType, key), null, expectedRevision);
    }

    // Checked before anything is kept, and encoded before the operation is kept: a refused operation leaves
    // the batch as it was.
    private void Add(WriteKindEnum kind, LatticeEntityType entityType, object key, object?[]? values, LatticeRevision? expectedRevision)
    {
        ThrowIfSubmitted();

        var entity = new WriteBatchEntity(entityType, key);

        if (_identities.Contains(entity))
        {
            throw new ArgumentException(
                $"The batch already has an operation for {entityType.Name} '{key}'. A batch takes one operation per entity.");
        }

        var root = _executor.EncodeRoot(entityType, values);

        _identities.Add(entity);
        _operations.Add(new WriteBatchOperation(kind, entityType, key, expectedRevision, root));
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

        _operations = [];
        _identities = [];

        return _executor.ExecuteAsync(operations, session, cancellationToken);
    }

    private void ThrowIfSubmitted()
    {
        if (_submitted)
        {
            throw new InvalidOperationException("The batch has already been submitted. Build a new batch.");
        }
    }
}
