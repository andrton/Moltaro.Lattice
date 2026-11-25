using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Sets;

// The entities of one entity type as objects of a .NET class, on the engine. It behaves as the
// contract of ILatticeSet<TEntity> says, as the set of the core does; it is the engine's own so that
// a read by key converts its key without boxing it and copies the root into the object through
// pooled scratch, without boxing a value of a field of the entity: a read allocates the returned
// object, with what is reachable from it, and nothing else, but for the form a record holds of an
// owned value or a collection, from which their objects are made. The constructors and the set accessors of
// the classes run after the read left the protection of reads (ClassCopier). A generated view uses
// the same stamped lookup as the view by name, with its handles bound before protection. A write takes the values of the object through the
// binding of the core, which checks them against the schema at every depth and takes a byte array of a
// field as the object holds it: the engine copies it once, into its own memory, while the write runs
// (EntityBinding.ValuesForWrite), since the application keeps the input of a write unchanged until the write
// completes (docs/decisions/mutation-commands.md, "Stable write inputs until completion").
internal sealed class ClassSet<TEntity> : ILatticeSet<TEntity>
    where TEntity : class
{
    private readonly SetOperations _operations;

    private readonly EntityBinding<TEntity> _binding;

    private readonly ClassCopier<TEntity> _copier;

    public ClassSet(SetOperations operations, EntityBinding<TEntity> binding)
    {
        _operations = operations;
        _binding = binding;
        _copier = new ClassCopier<TEntity>(binding, operations.Table.Layout);
    }

    public LatticeEntityType EntityType => _binding.EntityType;

    public LatticeUpdate CreateUpdate<TKey>(TKey key)
        where TKey : notnull =>
        _operations.CreateUpdate(key, _binding.Properties);

    public ILatticeIndex<TEntity> Index(string indexName) =>
        SetIndex<ClassCopy<TEntity>, TEntity>.Resolve(_operations, indexName, new ClassCopy<TEntity>(_copier));

    public TEntity? Get<TKey>(TKey key)
        where TKey : notnull =>
        TryGet(key, out var entity, out _) ? entity : null;

    public bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out TEntity entity)
        where TKey : notnull =>
        TryGet(key, out entity, out _);

    public bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out TEntity entity, out LatticeRevision revision)
        where TKey : notnull
    {
        var entityKey = KeyArgument.Of(_operations.Table, key);

        entity = _operations.TryRead<ClassCopy<TEntity>, TEntity>(entityKey, new ClassCopy<TEntity>(_copier), out revision, out _);

        return entity is not null;
    }

    public ValueTask<LatticeReadResult<TEntity>> GetAsync<TKey>(TKey key, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(session);

        return _operations.ReadAsync<ClassCopy<TEntity>, TEntity>(KeyArgument.Of(_operations.Table, key), new ClassCopy<TEntity>(_copier), session, cancellationToken);
    }

    public bool TryView<TKey, TView, TResult>(TKey key, LatticeViewFunc<TView, TResult> viewer, [MaybeNullWhen(false)] out TResult result)
        where TKey : notnull
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var entityKey = KeyArgument.Of(_operations.Table, key);
        var fields = ViewBinding<TView, TEntity>.For(EntityType);

        return _operations.TryView(entityKey, (fields, viewer), CallViewer<TView, TResult>, out result, out _);
    }

    public bool TryView<TKey, TView, TState, TResult>(TKey key, TState state, LatticeViewFunc<TView, TState, TResult> viewer,
        [MaybeNullWhen(false)] out TResult result)
        where TKey : notnull
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var entityKey = KeyArgument.Of(_operations.Table, key);
        var fields = ViewBinding<TView, TEntity>.For(EntityType);

        return _operations.TryView(entityKey, (fields, state, viewer), CallViewer<TView, TState, TResult>, out result, out _);
    }

    public ValueTask<LatticeViewResult<TResult>> ViewAsync<TKey, TView, TResult>(TKey key, LatticeSession session,
        LatticeViewFunc<TView, TResult> viewer, CancellationToken cancellationToken = default)
        where TKey : notnull
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewer);
        var entityKey = KeyArgument.Of(_operations.Table, key);

        return _operations.ViewAsync(entityKey, session, viewer, CallViewer<TView, TResult>,
            static (entityType, callback) => (ViewBinding<TView, TEntity>.For(entityType), callback), cancellationToken);
    }

    public ValueTask<LatticeViewResult<TResult>> ViewAsync<TKey, TView, TState, TResult>(TKey key, LatticeSession session, TState state,
        LatticeViewFunc<TView, TState, TResult> viewer, CancellationToken cancellationToken = default)
        where TKey : notnull
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewer);
        var entityKey = KeyArgument.Of(_operations.Table, key);

        return _operations.ViewAsync(entityKey, session, (state, viewer), CallViewer<TView, TState, TResult>,
            static (entityType, input) => (ViewBinding<TView, TEntity>.For(entityType), input.state, input.viewer), cancellationToken);
    }

    private static TResult CallViewer<TView, TResult>(scoped LatticeRecordView record, (LatticeField[] Fields, LatticeViewFunc<TView, TResult> Viewer) call)
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct =>
        call.Viewer(TView.Create(record, call.Fields));

    private static TResult CallViewer<TView, TState, TResult>(scoped LatticeRecordView record,
        (LatticeField[] Fields, TState State, LatticeViewFunc<TView, TState, TResult> Viewer) call)
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct =>
        call.Viewer(TView.Create(record, call.Fields), call.State);

    public IEnumerable<TEntity> Enumerate() =>
        _operations.Enumerate<ClassCopy<TEntity>, TEntity>(new ClassCopy<TEntity>(_copier));

    public IAsyncEnumerable<TEntity> EnumerateAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        return _operations.EnumerateAsync<ClassCopy<TEntity>, TEntity>(new ClassCopy<TEntity>(_copier), session, cancellationToken);
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

    // The arguments are checked first and the entity validated against the schema, its collections against
    // their bound included, before anything else can refuse the write.
    private ValueTask<LatticeWriteResult> Save(WriteKindEnum kind, TEntity entity, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        object?[] values;

        try
        {
            values = _binding.ValuesForWrite(entity);
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
