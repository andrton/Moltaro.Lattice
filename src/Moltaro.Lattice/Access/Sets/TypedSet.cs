using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Access.Sets;

// Entities as objects of a .NET class.
internal sealed class TypedSet<TEntity> : ILatticeSet<TEntity>
    where TEntity : class
{
    private readonly IEntityOperations _database;
    private readonly EntityBinding<TEntity> _binding;

    public TypedSet(IEntityOperations database, EntityBinding<TEntity> binding)
    {
        _database = database;
        _binding = binding;
    }

    public LatticeEntityType EntityType => _binding.EntityType;

    public ILatticeIndex<TEntity> Index(string indexName) =>
        SetIndex<TEntity>.Resolve(EntityType, indexName);

    public TEntity? Get<TKey>(TKey key)
        where TKey : notnull =>
        TryGet(key, out var entity, out _) ? entity : null;

    public bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out TEntity entity)
        where TKey : notnull =>
        TryGet(key, out entity, out _);

    public bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out TEntity entity, out LatticeRevision revision)
        where TKey : notnull
    {
        if (!_database.TryRead(EntityType, FieldValues.NormalizeKey(EntityType, key), out var stored, out _))
        {
            entity = null;
            revision = default;
            return false;
        }

        entity = _binding.Materialize(stored.Values);
        revision = stored.Revision;
        return true;
    }

    public ValueTask<LatticeReadResult<TEntity>> GetAsync<TKey>(TKey key, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(session);
        return ReadAsync(FieldValues.NormalizeKey(EntityType, key), session, cancellationToken);
    }

    public IEnumerable<TEntity> Enumerate()
    {
        foreach (var stored in _database.ReadAll(EntityType, out _))
        {
            yield return _binding.Materialize(stored.Values);
        }
    }

    // The limits checked their values when they were created; the placeholder cannot honor them.
    public IEnumerable<TEntity> Enumerate(LatticeReadLimits limits) =>
        throw new NotSupportedException("An enumeration with read limits is not implemented yet.");

    public IAsyncEnumerable<TEntity> EnumerateAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ReadAllAsync(session, cancellationToken);
    }

    public IAsyncEnumerable<TEntity> EnumerateAsync(LatticeSession session, LatticeReadLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        throw new NotSupportedException("An enumeration with read limits is not implemented yet.");
    }

    public ValueTask<LatticeWriteResult> InsertAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Insert, entity, null, null, cancellationToken);

    public ValueTask<LatticeWriteResult> InsertAsync(TEntity entity, LatticeSession session, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Insert, entity, null, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> ReplaceAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Replace, entity, null, null, cancellationToken);

    public ValueTask<LatticeWriteResult> ReplaceAsync(TEntity entity, LatticeSession session, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Replace, entity, null, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> ReplaceAsync(TEntity entity, LatticeRevision expectedRevision, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Replace, entity, expectedRevision, null, cancellationToken);

    public ValueTask<LatticeWriteResult> ReplaceAsync(TEntity entity, LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Replace, entity, expectedRevision, Required(session), cancellationToken);

    public ValueTask<LatticeWriteResult> UpsertAsync(TEntity entity, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Upsert, entity, null, null, cancellationToken);

    public ValueTask<LatticeWriteResult> UpsertAsync(TEntity entity, LatticeSession session, CancellationToken cancellationToken = default) =>
        Save(WriteKindEnum.Upsert, entity, null, Required(session), cancellationToken);

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

    // Wait first, read second: the entity is not looked at before this node has applied
    // the position the session depends on.
    private async ValueTask<LatticeReadResult<TEntity>> ReadAsync(object key, LatticeSession session, CancellationToken cancellationToken)
    {
        await _database.SynchronizeAsync(session, cancellationToken);

        var found = _database.TryRead(EntityType, key, out var stored, out var position);
        session.Advance(position);

        return found
            ? new LatticeReadResult<TEntity>(_binding.Materialize(stored.Values), stored.Revision, position)
            : new LatticeReadResult<TEntity>(null, default, position);
    }

    private async IAsyncEnumerable<TEntity> ReadAllAsync(LatticeSession session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await _database.SynchronizeAsync(session, cancellationToken);

        // The position covers every entity about to be returned, and an empty result too.
        var all = _database.ReadAll(EntityType, out var position);
        session.Advance(position);

        foreach (var stored in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return _binding.Materialize(stored.Values);
        }
    }

    private ValueTask<LatticeWriteResult> Save(WriteKindEnum kind, TEntity entity, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        object?[] values;
        try
        {
            values = _binding.Capture(entity);
        }
        catch (LatticeException exception)
        {
            return ValueTask.FromException<LatticeWriteResult>(exception);
        }

        return _database.WriteAsync(kind, EntityType, values[EntityType.Key.Ordinal]!, values, expectedRevision, session, cancellationToken);
    }

    private ValueTask<LatticeWriteResult> Delete<TKey>(TKey key, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken)
        where TKey : notnull =>
        _database.WriteAsync(WriteKindEnum.Delete, EntityType, FieldValues.NormalizeKey(EntityType, key), null, expectedRevision, session, cancellationToken);
}
