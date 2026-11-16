using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Sets;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// The entities of one entity type by name, as records, on the engine. It behaves as the contract of
// ILatticeSet says, member for member as ClassSet does; a read allocates the returned record, with
// what it holds, and nothing else. A key boxed by the caller, as the signatures that take object
// have it, is the caller's allocation (brief of S06, "The allocation of a read"). A write takes the
// byte arrays of the fields of a record as the record holds them, and the engine copies each once,
// into its own memory, while the write runs (LatticeRecord.Capture; the planner's answer to Q43).
internal sealed class RecordSet : ILatticeSet
{
    private readonly SetOperations _operations;

    public RecordSet(SetOperations operations)
    {
        _operations = operations;
    }

    public LatticeEntityType EntityType => _operations.Table.EntityType;

    public ILatticeIndex<LatticeRecord> Index(string indexName) =>
        SetIndex<LatticeRecord>.Resolve(EntityType, indexName);

    public LatticeRecord? Get(object key) => TryGet(key, out var record, out _) ? record : null;

    public bool TryGet(object key, [MaybeNullWhen(false)] out LatticeRecord record) => TryGet(key, out record, out _);

    public bool TryGet(object key, [MaybeNullWhen(false)] out LatticeRecord record, out LatticeRevision revision)
    {
        var entityKey = KeyArgument.Of(_operations.Table, key);

        record = _operations.TryRead<RecordCopy, LatticeRecord>(entityKey, new RecordCopy(EntityType), out revision, out _);

        return record is not null;
    }

    public ValueTask<LatticeReadResult<LatticeRecord>> GetAsync(object key, LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _operations.ReadAsync<RecordCopy, LatticeRecord>(KeyArgument.Of(_operations.Table, key), new RecordCopy(EntityType), session, cancellationToken);
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

    public ValueTask<LatticeWriteResult> DeleteAsync(object key, CancellationToken cancellationToken = default) =>
        Delete(key, null, null, cancellationToken);

    public ValueTask<LatticeWriteResult> DeleteAsync(object key, LatticeSession session, CancellationToken cancellationToken = default) =>
        Delete(key, null, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> DeleteAsync(object key, LatticeRevision expectedRevision, CancellationToken cancellationToken = default) =>
        Delete(key, expectedRevision, null, cancellationToken);

    public ValueTask<LatticeWriteResult> DeleteAsync(object key, LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default) =>
        Delete(key, expectedRevision, Required(session), cancellationToken);

    private static LatticeSession Required(LatticeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session;
    }

    // The arguments are checked first and the record validated against the schema, before anything
    // else can refuse the write (Q25).
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
            values = record.Capture(copyBinary: false);
        }
        catch (LatticeException exception)
        {
            return ValueTask.FromException<LatticeWriteResult>(exception);
        }

        return _operations.WriteAsync(kind, KeyArgument.OfValues(_operations.Table, values), values, expectedRevision, session, cancellationToken);
    }

    private ValueTask<LatticeWriteResult> Delete(object key, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken) =>
        _operations.WriteAsync(WriteKindEnum.Delete, KeyArgument.Of(_operations.Table, key), null, expectedRevision, session, cancellationToken);
}
