using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Sets;

// One declared index of a set. The same class serves entity classes and records.
//
// Only the contract is in place: the index is resolved from the schema and a key is checked
// against it, which allocates nothing for a key it accepts (IndexCriteria). The search itself is not
// implemented; it arrives with the engine's ordered secondary indexes.
internal sealed class SetIndex<TEntity> : ILatticeIndex<TEntity>
    where TEntity : class
{
    private SetIndex(LatticeIndex definition)
    {
        Definition = definition;
    }

    public LatticeIndex Definition { get; }

    public static SetIndex<TEntity> Resolve(LatticeEntityType entityType, string indexName)
    {
        ArgumentException.ThrowIfNullOrEmpty(indexName);

        return entityType.TryGetIndex(indexName, out var definition)
            ? new SetIndex<TEntity>(definition)
            : throw new LatticeException(
                LatticeErrorEnum.BindingFailed,
                $"Entity type '{entityType.Name}' declares no index '{indexName}'. Declare it in the schema; nothing is searched without an index.");
    }

    public IEnumerable<TEntity> Find(IndexKey key)
    {
        _ = IndexCriteria.Prepare(Definition, key);
        throw NotImplemented();
    }

    public ValueTask<LatticeFindResult<TEntity>> FindAsync(IndexKey key, LatticeFindOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = IndexCriteria.Prepare(Definition, key);
        throw NotImplemented();
    }

    private static NotSupportedException NotImplemented() =>
        new("Searching an index is not implemented yet.");
}
