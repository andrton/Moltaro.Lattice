using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Sets;

// The entities of one entity type as objects of a .NET class, on the engine. It behaves as the
// contract of ILatticeSet<TEntity> says, as the set of the core does; it is the engine's own so that
// a read by key converts its key without boxing it and copies the root into the object through
// pooled scratch, without boxing a value: a read allocates the returned object, with what is
// reachable from it, and nothing else. The constructor and the set accessors of the class run after the
// read left the protection of reads (ClassCopier). A write takes the values of the object through the
// binding of the core, which checks them against the schema at every depth and takes a byte array of a
// field as the object holds it: the engine copies it once, into its own memory, while the write runs
// (EntityBinding.ValuesForWrite), since the application keeps the input of a write unchanged until the write
// completes (docs/decisions/mutation-commands.md, "Stable write inputs until completion").
internal sealed class ClassSet<TEntity> : ILatticeSet<TEntity>
    where TEntity : class
{
    private readonly SetOperations _operations;

    private readonly EntityBinding<TEntity> _binding;

    // Null for an entity type the engine does not store, nothing of which is ever read, and for one whose
    // reads are refused (RefusesReads).
    private readonly ClassCopier<TEntity>? _copier;

    public ClassSet(SetOperations operations, EntityBinding<TEntity> binding)
    {
        _operations = operations;
        _binding = binding;
        RefusesReads = operations.Table.Layout is { Structure.HoldsOwnedValuesOrLists: true };
        _copier = operations.Table.Layout is { } layout && !RefusesReads ? new ClassCopier<TEntity>(binding, layout) : null;
    }

    // Whether every read through the class is refused before it reads: building an object of an entity type
    // that holds an owned value or a List from its root is not implemented yet. A synchronous read and Index
    // throw the refusal at the call; an asynchronous read reports it through its task or its enumeration,
    // since it throws at the call only what its arguments show.
    private bool RefusesReads { get; }

    // The copier of a read: the one of the set, null for an entity type the engine does not store, whose read
    // finds nothing; the refusal of a read for one whose reads are refused.
    private ClassCopier<TEntity> Copier => _copier ?? NoCopier();

    // The copier of a set that has none: null for an entity type the engine does not store, the refusal for
    // one whose reads are refused.
    private ClassCopier<TEntity> NoCopier() => RefusesReads ? throw ReadRefusal() : null!;

    private NotSupportedException ReadRefusal() =>
        new($"A read of {_binding.EntityType.Name} through a class is not implemented yet: it holds owned values or Lists. Read it by name.");

    public LatticeEntityType EntityType => _binding.EntityType;

    public ILatticeIndex<TEntity> Index(string indexName) =>
        SetIndex<ClassCopy<TEntity>, TEntity>.Resolve(_operations, indexName, new ClassCopy<TEntity>(Copier));

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

        entity = _operations.TryRead<ClassCopy<TEntity>, TEntity>(entityKey, new ClassCopy<TEntity>(Copier), out revision, out _);

        return entity is not null;
    }

    public ValueTask<LatticeReadResult<TEntity>> GetAsync<TKey>(TKey key, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(session);

        var entityKey = KeyArgument.Of(_operations.Table, key);

        if (RefusesReads)
        {
            return ValueTask.FromException<LatticeReadResult<TEntity>>(ReadRefusal());
        }

        return _operations.ReadAsync<ClassCopy<TEntity>, TEntity>(entityKey, new ClassCopy<TEntity>(Copier), session, cancellationToken);
    }

    public IEnumerable<TEntity> Enumerate() =>
        _operations.Enumerate<ClassCopy<TEntity>, TEntity>(new ClassCopy<TEntity>(Copier));

    public IAsyncEnumerable<TEntity> EnumerateAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (RefusesReads)
        {
            return Refused(ReadRefusal());
        }

        return _operations.EnumerateAsync<ClassCopy<TEntity>, TEntity>(new ClassCopy<TEntity>(Copier), session, cancellationToken);
    }

    // An enumeration whose first step fails with the refusal: awaiting the failed task throws it.
    private static async IAsyncEnumerable<TEntity> Refused(NotSupportedException refusal)
    {
        await Task.FromException(refusal).ConfigureAwait(false);

        yield break;
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

    // The arguments are checked first and the entity validated against the schema, before anything
    // else can refuse the write.
    private ValueTask<LatticeWriteResult> Save(WriteKindEnum kind, TEntity entity, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);

        object?[] values;

        try
        {
            values = _binding.ValuesForWrite(entity);
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
