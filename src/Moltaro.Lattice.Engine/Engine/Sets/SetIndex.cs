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
// index cannot serve is ArgumentException, and the default of IndexKey is no key. A search is a walk of the
// index by the leading values of the key and a range on the next field, in the index order or its exact
// reverse, a step at a time (Entities.EntityTable.TrySearch); nothing is answered by reading every entity
// instead. FindAsync throws at the call only what the caller can know from its own arguments: no options, a
// key the index cannot serve, the default of IndexKey; everything that depends on the state of the database
// comes through the task it returns, also on a path that needs no wait, a close among them, as in every
// asynchronous member. Find is lazy, one object that is its result and its first enumerator, and every
// enumerator it gives is a search of its own (IndexMatches); FindAsync waits for the position of its session
// first, outside every lock and the protection of reads, and then runs the search to its end, with the
// search on its stack (IndexSearch): it skips the matches of its Offset, takes at most its Limit, and asks
// once more whether a match follows (HasMore), and it holds its answer against the memory limit of a read
// once the search remembers more than one step.
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
        new IndexMatches<TCopy, TEntity>(_operations, _index, IndexCriteria.Prepare(Definition, key), _prototype);

    public ValueTask<LatticeFindResult<TEntity>> FindAsync(IndexKey key, LatticeFindOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return FindAfterAsync(IndexCriteria.Prepare(Definition, key), options, cancellationToken);
    }

    // The search of a key the call checked, after the position of the session when it has one, every
    // failure through the task (see the class). In a Release build an asynchronous method allocates nothing
    // when it completes within its call, as a search without a wait does: its state machine is a struct on
    // the stack. A Debug build makes the state machine a class and allocates it at every call.
    private async ValueTask<LatticeFindResult<TEntity>> FindAfterAsync(IndexKey bound, LatticeFindOptions options, CancellationToken cancellationToken)
    {
        if (options.Session is { } session)
        {
            await _operations.WaitAsync(session, cancellationToken).ConfigureAwait(false);
        }

        return FindAll(bound, options, cancellationToken);
    }

    // The entities of the key the options ask for, a step of the search at a time, each built outside the
    // protection of reads; the position of the answer covers every step, its end included, and the session
    // moves to it. With a Limit reached, one look more says whether a match follows.
    private LatticeFindResult<TEntity> FindAll(in IndexKey bound, LatticeFindOptions options, CancellationToken cancellationToken)
    {
        var search = new IndexSearch(_operations.Options, _index is not null && IndexWalk.IsFullKey(_index.Layout, bound), options.Reverse, options.Offset);
        var items = new List<TEntity>();
        var last = 0UL;
        var hasMore = false;

        while (options.Limit is not { } limit || items.Count < limit)
        {
            var found = _operations.Next<TCopy, TEntity>(ref search, _index, bound, _prototype, out var sequence, cancellationToken);

            last = Math.Max(last, sequence);

            if (found is null)
            {
                break;
            }

            // The list grows as List<T> grows it, to 4 and then by doubling, but here, so that its growth counts
            // against the memory limit before it is made (docs/decisions/queries.md, Q07.1.1, "check before
            // growing workspace"); Add then never grows it.
            if (items.Count == items.Capacity)
            {
                var capacity = items.Capacity == 0 ? 4 : (int)Math.Min(2L * items.Capacity, Array.MaxLength);

                _operations.Hold(ref search, _index, (long)(capacity - items.Capacity) * IntPtr.Size, cancellationToken);
                items.Capacity = capacity;
            }

            items.Add(found);
        }

        if (options.Limit is { } most && items.Count == most)
        {
            hasMore = _operations.HasMore(ref search, _index, bound, out var sequence, cancellationToken);
            last = Math.Max(last, sequence);
        }

        var position = _operations.At(last);

        options.Session?.Advance(position);

        return new LatticeFindResult<TEntity>(items, hasMore, position);
    }
}
