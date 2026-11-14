using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Access;

// Entities by name, as records.
internal sealed class DynamicSet : ILatticeSet
{
    private readonly IEntityOperations _database;

    public DynamicSet(IEntityOperations database, LatticeEntityType entityType)
    {
        _database = database;
        EntityType = entityType;
    }

    public LatticeEntityType EntityType { get; }

    public ILatticeIndex<LatticeRecord> Index(string indexName) =>
        SetIndex<LatticeRecord>.Resolve(EntityType, indexName);

    public LatticeRecord? Get(object key) => TryGet(key, out var record, out _) ? record : null;

    public bool TryGet(object key, [MaybeNullWhen(false)] out LatticeRecord record) => TryGet(key, out record, out _);

    public bool TryGet(object key, [MaybeNullWhen(false)] out LatticeRecord record, out LatticeRevision revision)
    {
        if (!_database.TryRead(EntityType, FieldValues.NormalizeKey(EntityType, key), out var stored, out _))
        {
            record = null;
            revision = default;
            return false;
        }

        record = new LatticeRecord(EntityType, stored.Values);
        revision = stored.Revision;
        return true;
    }

    public ValueTask<LatticeReadResult<LatticeRecord>> GetAsync(object key, LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ReadAsync(FieldValues.NormalizeKey(EntityType, key), session, cancellationToken);
    }

    public IEnumerable<LatticeRecord> Enumerate()
    {
        foreach (var stored in _database.ReadAll(EntityType, out _))
        {
            yield return new LatticeRecord(EntityType, stored.Values);
        }
    }

    public IAsyncEnumerable<LatticeRecord> EnumerateAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ReadAllAsync(session, cancellationToken);
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

    // Wait first, read second: the entity is not looked at before this node has applied
    // the position the session depends on.
    private async ValueTask<LatticeReadResult<LatticeRecord>> ReadAsync(object key, LatticeSession session, CancellationToken cancellationToken)
    {
        await _database.SynchronizeAsync(session, cancellationToken);

        var found = _database.TryRead(EntityType, key, out var stored, out var position);
        session.Advance(position);

        return found
            ? new LatticeReadResult<LatticeRecord>(new LatticeRecord(EntityType, stored.Values), stored.Revision, position)
            : new LatticeReadResult<LatticeRecord>(null, default, position);
    }

    private async IAsyncEnumerable<LatticeRecord> ReadAllAsync(LatticeSession session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _database.SynchronizeAsync(session, cancellationToken);

        // The position covers every record about to be returned, and an empty result too.
        var all = _database.ReadAll(EntityType, out var position);
        session.Advance(position);

        foreach (var stored in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new LatticeRecord(EntityType, stored.Values);
        }
    }

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
            values = record.Capture();
        }
        catch (LatticeException exception)
        {
            return ValueTask.FromException<LatticeWriteResult>(exception);
        }

        return _database.WriteAsync(kind, EntityType, values[EntityType.Key.Ordinal]!, values, expectedRevision, session, cancellationToken);
    }

    private ValueTask<LatticeWriteResult> Delete(object key, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken) =>
        _database.WriteAsync(WriteKindEnum.Delete, EntityType, FieldValues.NormalizeKey(EntityType, key), null, expectedRevision, session, cancellationToken);
}
