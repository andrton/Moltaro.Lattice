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
//
// Memory. Every member is charged to the budget of the instance by an estimate, its encoded length and
// BatchInputCharge.PerMemberBytes, and the first one also by BatchInputCharge.PerBuilderBytes; the
// estimate is reserved last, after every check of the addition and its snapshot, so a refusal for memory
// comes after every other refusal and changes nothing. The one charge object returns the whole charge when
// the execution of the batch ends, or when the batch is collected uncommitted.
internal sealed class WriteBatch : ILatticeWriteBatch
{
    // The room of the operations when the first is added; it doubles when it is full.
    private const int FirstRoom = 4;

    private readonly LatticeSchema _schema;

    private readonly IWriteBatchExecutor _executor;

    private readonly BatchInputCharge _charge;

    // The operations in the order they were added: the first Count.
    private WriteBatchOperation[] _operations = [];

    // The entities the batch already addresses, told apart as the schema compares their keys
    // (WriteBatchEntity). A key is normalized to the key type of its entity type before it is
    // added, so an integer key is one entity in whatever width it was given.
    private HashSet<WriteBatchEntity> _identities = [];
    private bool _submitted;
    private bool _hasUpdates;

    public WriteBatch(LatticeSchema schema, IWriteBatchExecutor executor)
    {
        _schema = schema;
        _executor = executor;
        _charge = new BatchInputCharge(executor);
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
    // the batch as it was. The refusals come in this order: the validation of the entity by the core,
    // before Add (Save: a String or Binary value above its bound and every other check of the schema);
    // then a batch already submitted, InvalidOperationException; then an operation for an entity the
    // batch already addresses; then, as the engine encodes the root (IWriteBatchExecutor.EncodeRoot), a
    // database that was closed, ObjectDisposedException, and a List above its bound; last the memory of
    // the instance for the estimate of the member (Reserve).
    private void Add(WriteKindEnum kind, LatticeEntityType entityType, object key, object?[]? values, LatticeRevision? expectedRevision)
    {
        var entity = CheckIdentity(entityType, key);
        var root = _executor.EncodeRoot(entityType, values);
        Reserve(root.Length);
        Keep(entity, new WriteBatchOperation(kind, entityType, key, expectedRevision, root));
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
        var input = update.ValidateContent();
        var entityType = update.EntityType;
        var key = update.Key;
        var entity = CheckIdentity(entityType, key);
        var length = _executor.CheckUpdate(entityType, input);
        Reserve(length);
        Keep(entity, new WriteBatchOperation(WriteKindEnum.Update, entityType, key, expectedRevision, default, input));
        _hasUpdates = true;
        update.Consume();
    }

    // The last step of an addition, once its checks are made and its snapshot is built: room for one more
    // member, then the estimate of the member, its encoded length and PerMemberBytes, with PerBuilderBytes
    // for the first, in the budget of the instance. WriteRejected with Memory when the instance cannot hold
    // it, ObjectDisposedException when the database closed meanwhile; the member is not kept then, and the
    // snapshot is the collector's.
    private void Reserve(long length)
    {
        // Finish any fallible allocation before the reservation and before changing either builder.
        if (Count == _operations.Length)
        {
            Array.Resize(ref _operations, Math.Max(FirstRoom, 2 * _operations.Length));
        }

        _identities.EnsureCapacity(Count + 1);

        var estimate = length + BatchInputCharge.PerMemberBytes + (Count == 0 ? BatchInputCharge.PerBuilderBytes : 0);
        if (!_charge.TryAdd(estimate))
        {
            ObjectDisposedException.ThrowIf(_executor.IsClosed, _executor);
            throw new LatticeException(LatticeErrorEnum.WriteRejected, "The write needs more memory than the instance can hold.")
            {
                Limit = LatticeLimitEnum.Memory,
            };
        }
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
        _operations[Count] = operation;
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
    // The charge of the batch passes to its execution: it is returned when the execution ends, whatever
    // it ends with, unless the execution returned it before (IWriteBatchExecutor.ExecuteAsync).
    private ValueTask<LatticeWriteResult> Commit(LatticeSession? session, CancellationToken cancellationToken)
    {
        ThrowIfSubmitted();
        if (Count == 0)
        {
            throw new InvalidOperationException("The batch is empty; there is nothing to commit.");
        }

        _submitted = true;

        var operations = new ArraySegment<WriteBatchOperation>(_operations, 0, Count);

        _operations = [];
        _identities = [];

        ValueTask<LatticeWriteResult> result;
        try
        {
            result = _executor.ExecuteAsync(operations, _charge, session, cancellationToken);
        }
        catch
        {
            _charge.Dispose();
            throw;
        }

        if (_hasUpdates)
        {
            var completion = result.AsTask();
            foreach (var operation in operations)
            {
                operation.Update?.Outcome.Attach(completion);
            }

            result = new ValueTask<LatticeWriteResult>(completion);
        }

        return ReturnChargeAfterAsync(result, _charge);
    }

    private static async ValueTask<LatticeWriteResult> ReturnChargeAfterAsync(ValueTask<LatticeWriteResult> result, BatchInputCharge charge)
    {
        try
        {
            return await result.ConfigureAwait(false);
        }
        finally
        {
            charge.Dispose();
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
