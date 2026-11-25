using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Sets;

// The entities of one entity type by name, as records, on the engine. It behaves as the contract of
// ILatticeSet says, member for member as ClassSet does; a read allocates the returned record, with
// what it holds, and nothing else. A key given to a read or a delete is converted as ClassSet
// converts it, without a box (KeyArgument). A write takes the byte arrays of the fields of a record
// as the record holds them, and the engine copies each once, into its own memory, while the write runs
// (LatticeRecord.ValuesForWrite), since the application keeps the input of a write unchanged until the write
// completes (docs/decisions/mutation-commands.md, "Stable write inputs until completion").
internal sealed class RecordSet : ILatticeSet
{
    private const string ProtectedViewsPending = "Protected views are not implemented yet.";

    private readonly SetOperations _operations;

    public RecordSet(SetOperations operations)
    {
        _operations = operations;
    }

    public LatticeEntityType EntityType => _operations.Table.EntityType;

    public LatticeUpdate CreateUpdate<TKey>(TKey key)
        where TKey : notnull =>
        _operations.CreateUpdate(key);

    public ILatticeIndex<LatticeRecord> Index(string indexName) =>
        SetIndex<RecordCopy, LatticeRecord>.Resolve(_operations, indexName, new RecordCopy(EntityType));

    public LatticeRecord? Get<TKey>(TKey key)
        where TKey : notnull =>
        TryGet(key, out var record, out _) ? record : null;

    public bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out LatticeRecord record)
        where TKey : notnull =>
        TryGet(key, out record, out _);

    public bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out LatticeRecord record, out LatticeRevision revision)
        where TKey : notnull
    {
        var entityKey = KeyArgument.Of(_operations.Table, key);

        record = _operations.TryRead<RecordCopy, LatticeRecord>(entityKey, new RecordCopy(EntityType), out revision, out _);

        return record is not null;
    }

    public ValueTask<LatticeReadResult<LatticeRecord>> GetAsync<TKey>(TKey key, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(session);

        return _operations.ReadAsync<RecordCopy, LatticeRecord>(KeyArgument.Of(_operations.Table, key), new RecordCopy(EntityType), session, cancellationToken);
    }

    public bool TryView<TKey, TResult>(TKey key, LatticeViewFunc<LatticeRecordView, TResult> viewer, [MaybeNullWhen(false)] out TResult result)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(viewer);
        KeyArgument.Of(_operations.Table, key);

        throw new NotSupportedException(ProtectedViewsPending);
    }

    public bool TryView<TKey, TState, TResult>(TKey key, TState state, LatticeViewFunc<LatticeRecordView, TState, TResult> viewer, [MaybeNullWhen(false)] out TResult result)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(viewer);
        KeyArgument.Of(_operations.Table, key);

        throw new NotSupportedException(ProtectedViewsPending);
    }

    public ValueTask<LatticeViewResult<TResult>> ViewAsync<TKey, TResult>(TKey key, LatticeSession session, LatticeViewFunc<LatticeRecordView, TResult> viewer, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewer);
        KeyArgument.Of(_operations.Table, key);

        throw new NotSupportedException(ProtectedViewsPending);
    }

    public ValueTask<LatticeViewResult<TResult>> ViewAsync<TKey, TState, TResult>(TKey key, LatticeSession session, TState state, LatticeViewFunc<LatticeRecordView, TState, TResult> viewer, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewer);
        KeyArgument.Of(_operations.Table, key);

        throw new NotSupportedException(ProtectedViewsPending);
    }

    public IEnumerable<LatticeRecord> Enumerate() =>
        _operations.Enumerate<RecordCopy, LatticeRecord>(new RecordCopy(EntityType));

    public IAsyncEnumerable<LatticeRecord> EnumerateAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _operations.EnumerateAsync<RecordCopy, LatticeRecord>(new RecordCopy(EntityType), session, cancellationToken);
    }

    public ValueTask<LatticeWriteResult> InsertAsync(LatticeRecord record, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Insert, record, null, null, cancellationToken);

    public ValueTask<LatticeWriteResult> InsertAsync(LatticeRecord record, LatticeSession session, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Insert, record, null, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> ReplaceAsync(LatticeRecord record, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Replace, record, null, null, cancellationToken);

    public ValueTask<LatticeWriteResult> ReplaceAsync(LatticeRecord record, LatticeSession session, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Replace, record, null, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> ReplaceAsync(LatticeRecord record, LatticeRevision expectedRevision, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Replace, record, expectedRevision, null, cancellationToken);

    public ValueTask<LatticeWriteResult> ReplaceAsync(LatticeRecord record, LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Replace, record, expectedRevision, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> UpsertAsync(LatticeRecord record, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Upsert, record, null, null, cancellationToken);

    public ValueTask<LatticeWriteResult> UpsertAsync(LatticeRecord record, LatticeSession session, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Upsert, record, null, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> DeleteAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
        where TKey : notnull =>
        Delete(key, null, null, cancellationToken);

    public ValueTask<LatticeWriteResult> DeleteAsync<TKey>(TKey key, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull =>
        Delete(key, null, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> DeleteAsync<TKey>(TKey key, LatticeRevision expectedRevision, CancellationToken cancellationToken = default)
        where TKey : notnull =>
        Delete(key, expectedRevision, null, cancellationToken);

    public ValueTask<LatticeWriteResult> DeleteAsync<TKey>(TKey key, LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull =>
        Delete(key, expectedRevision, Required(session), cancellationToken);

    private static LatticeSession Required(LatticeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session;
    }

    // The arguments are checked first and the record validated against the schema, its collections against
    // their bound included, before anything else can refuse the write.
    private ValueTask<LatticeWriteResult> Save(WriteKindEnum kind, LatticeRecord record, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!ReferenceEquals(record.EntityType, EntityType))
        {
            throw new ArgumentException($"The record is a '{record.EntityType.Name}' of another schema or entity type, not a '{EntityType.Name}' of this database.", nameof(record));
        }

        object?[] values;

        try
        {
            values = record.ValuesForWrite();
            _operations.CheckCollectionBounds(values);
        }
        catch (LatticeException exception)
        {
            return ValueTask.FromException<LatticeWriteResult>(exception);
        }

        return _operations.WriteAsync(kind, KeyArgument.OfValues(_operations.Table, values), values, expectedRevision, session, cancellationToken);
    }

    private ValueTask<LatticeWriteResult> Delete<TKey>(TKey key, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken)
        where TKey : notnull =>
        _operations.WriteAsync(WriteKindEnum.Delete, KeyArgument.Of(_operations.Table, key), null, expectedRevision, session, cancellationToken);
}
