using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Sets;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Sets;

// One declared index of a set, on the engine, for either copy: an object of a class (ClassCopy) or a
// record (RecordCopy), as ILatticeIndex<TEntity> says. It is resolved from the schema by its name, once
// for the object, and holds the secondary index of the table that keeps it (Indexes.SecondaryIndex), or
// none for an entity type the engine does not store, whose search finds nothing, as a read of it does.
//
// A key is checked against the index before anything is searched (Access.Sets.IndexCriteria): a key the
// index cannot serve is ArgumentException, and the default of IndexKey is no key. The search by the full
// key of the index is built; a search by its leading fields, with a range, in reverse, or with Offset or
// Limit is the walk of ranges, which is not built, and is refused with NotSupportedException; nothing is
// answered by reading every entity instead. Find is lazy, and every enumerator it gives is a search of
// its own (IndexMatches); FindAsync waits for the position of its session first, outside every lock and
// the protection of reads, and then runs the search to its end (IndexSearch).
//
// It holds no state that changes and keeps nothing of a search, so it is safe to keep and to share
// between threads, and the database holds no reference to it.
internal sealed class SetIndex<TCopy, TEntity> : ILatticeIndex<TEntity>
    where TCopy : struct, IRootCopy<TEntity>
    where TEntity : class
{
    private readonly SetOperations _operations;

    // Null when the engine does not store the entity type.
    private readonly SecondaryIndex? _index;

    private readonly TCopy _prototype;

    private SetIndex(SetOperations operations, LatticeIndex definition, TCopy prototype)
    {
        _operations = operations;
        _prototype = prototype;
        _index = operations.Table.IndexOf(definition);
        Definition = definition;
    }

    public LatticeIndex Definition { get; }

    // The index of the name the entity type of the set declares, with the copy the set makes of a root;
    // BindingFailed when it declares none of that name: there is no search without an index.
    public static SetIndex<TCopy, TEntity> Resolve(SetOperations operations, string indexName, TCopy prototype)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexName);

        var entityType = operations.Table.EntityType;

        return entityType.TryGetIndex(indexName, out var definition)
            ? new SetIndex<TCopy, TEntity>(operations, definition, prototype)
            : throw new LatticeException(
                LatticeErrorEnum.BindingFailed,
                $"Entity type '{entityType.Name}' declares no index '{indexName}'. Declare it in the schema; nothing is searched without an index.");
    }

    public IEnumerable<TEntity> Find(IndexKey key) =>
        new IndexMatches<TCopy, TEntity>(_operations, _index, Prepare(key, null), _prototype);

    public ValueTask<LatticeFindResult<TEntity>> FindAsync(IndexKey key, LatticeFindOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bound = Prepare(key, options);

        if (options.Session is { } session)
        {
            var wait = _operations.WaitAsync(session, cancellationToken);

            if (!wait.IsCompletedSuccessfully)
            {
                return FindAfterAsync(wait, bound, session, cancellationToken);
            }

            wait.GetAwaiter().GetResult();
        }

        return ValueTask.FromResult(FindAll(bound, options.Session, cancellationToken));
    }

    // The key checked against the index and bound to its fields, with the options of the search; the
    // shapes of the walk of ranges are refused (see the class).
    private IndexKey Prepare(in IndexKey key, LatticeFindOptions? options)
    {
        var bound = IndexCriteria.Prepare(Definition, key);

        if (bound.Count < Definition.Parts.Count || bound.From is not null || bound.To is not null)
        {
            throw new NotSupportedException($"Index '{Definition.Name}': a search by the leading fields of an index, or with a range, is not implemented yet; a search gives a value to each of its {Definition.Parts.Count} field(s).");
        }

        if (options is not null && (options.Reverse || options.Offset > 0 || options.Limit is not null))
        {
            throw new NotSupportedException($"Index '{Definition.Name}': a search in reverse, or with Offset or Limit, is not implemented yet.");
        }

        return bound;
    }

    private async ValueTask<LatticeFindResult<TEntity>> FindAfterAsync(ValueTask wait, IndexKey bound, LatticeSession session, CancellationToken cancellationToken)
    {
        await wait.ConfigureAwait(false);

        return FindAll(bound, session, cancellationToken);
    }

    // Every entity of the key, a step of the search at a time, each built outside the protection of
    // reads; the position of the answer covers every step, its end included, and the session moves to it.
    private LatticeFindResult<TEntity> FindAll(in IndexKey bound, LatticeSession? session, CancellationToken cancellationToken)
    {
        var search = new IndexSearch(_operations.Table, _index, bound);
        var items = new List<TEntity>();
        var last = 0UL;

        while (true)
        {
            var found = _operations.Next<TCopy, TEntity>(search, _prototype, out var sequence, cancellationToken);

            last = Math.Max(last, sequence);

            if (found is null)
            {
                break;
            }

            items.Add(found);
        }

        var position = _operations.At(last);

        session?.Advance(position);

        return new LatticeFindResult<TEntity>(items, hasMore: false, position);
    }
}
