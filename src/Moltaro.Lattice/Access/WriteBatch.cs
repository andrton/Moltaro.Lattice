using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Access;

// A write batch under construction: the operations in the order they were added, each
// already checked against the schema and copied from the caller's object.
//
// Only the contract is in place: a batch is built and validated, but not executed. The
// execution arrives with the engine's write coordinator, which publishes a whole batch at
// one position.
internal sealed class WriteBatch : ILatticeWriteBatch
{
    private readonly LatticeSchema _schema;
    private readonly List<WriteBatchOperation> _operations = [];

    // The entities the batch already addresses. A key is normalized to the key type of its
    // entity type, so equal keys are equal objects here.
    private readonly HashSet<(LatticeEntityType EntityType, object Key)> _identities = [];
    private bool _submitted;

    public WriteBatch(LatticeSchema schema)
    {
        _schema = schema;
    }

    public int Count => _operations.Count;

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

    public void Delete(string entityName, object key) => Delete(Resolve(entityName), key, null);

    public void Delete(string entityName, object key, LatticeRevision expectedRevision) => Delete(Resolve(entityName), key, expectedRevision);

    public ValueTask<LatticeWriteResult> CommitAsync(CancellationToken cancellationToken = default) =>
        Commit();

    public ValueTask<LatticeWriteResult> CommitAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Commit();
    }

    private void Save<TEntity>(WriteKindEnum kind, TEntity entity, LatticeRevision? expectedRevision)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);

        var binding = EntityBinding<TEntity>.For(_schema);
        Add(kind, binding.EntityType, binding.Capture(entity), expectedRevision);
    }

    private void Save(WriteKindEnum kind, LatticeRecord record, LatticeRevision? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_schema.TryGetEntityType(record.EntityType.Name, out var entityType) || !ReferenceEquals(entityType, record.EntityType))
        {
            throw new ArgumentException($"The record is a '{record.EntityType.Name}' of another schema, not of this database.", nameof(record));
        }

        Add(kind, entityType, record.Capture(), expectedRevision);
    }

    private void Add(WriteKindEnum kind, LatticeEntityType entityType, object?[] values, LatticeRevision? expectedRevision)
    {
        var key = values[entityType.Key.Ordinal]!;
        Add(new WriteBatchOperation(kind, entityType, key, values, expectedRevision));
    }

    private void Delete(LatticeEntityType entityType, object key, LatticeRevision? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(key);
        Add(new WriteBatchOperation(WriteKindEnum.Delete, entityType, FieldValues.NormalizeKey(entityType, key), null, expectedRevision));
    }

    private void Add(WriteBatchOperation operation)
    {
        ThrowIfSubmitted();

        if (!_identities.Add((operation.EntityType, operation.Key)))
        {
            throw new ArgumentException(
                $"The batch already has an operation for {operation.EntityType.Name} '{operation.Key}'. A batch takes one operation per entity.");
        }

        _operations.Add(operation);
    }

    private LatticeEntityType Resolve(string entityName)
    {
        ArgumentException.ThrowIfNullOrEmpty(entityName);

        return _schema.TryGetEntityType(entityName, out var entityType)
            ? entityType
            : throw new LatticeException(LatticeErrorEnum.BindingFailed, $"The schema has no entity type '{entityName}'.");
    }

    private ValueTask<LatticeWriteResult> Commit()
    {
        ThrowIfSubmitted();
        if (_operations.Count == 0)
        {
            throw new InvalidOperationException("The batch is empty; there is nothing to commit.");
        }

        _submitted = true;
        throw new NotSupportedException("Executing a write batch is not implemented yet.");
    }

    private void ThrowIfSubmitted()
    {
        if (_submitted)
        {
            throw new InvalidOperationException("The batch has already been submitted. Build a new batch.");
        }
    }
}
