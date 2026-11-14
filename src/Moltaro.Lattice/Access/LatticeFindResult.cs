namespace Moltaro.Lattice.Access;

/// <summary>
/// The result of an index search: the matching entities and the position the answer is
/// based on. "Nothing found" is a result too and has a position.
/// </summary>
/// <typeparam name="TEntity">The entity class, or <see cref="LatticeRecord"/>.</typeparam>
public readonly struct LatticeFindResult<TEntity>
    where TEntity : class
{
    internal LatticeFindResult(IReadOnlyList<TEntity> items, bool hasMore, LatticePosition position)
    {
        Items = items;
        HasMore = hasMore;
        Position = position;
    }

    /// <summary>
    /// Owned copies of the matching entities, in the requested order. Entities with the
    /// same indexed values come in no particular order among themselves.
    /// </summary>
    public IReadOnlyList<TEntity> Items { get; }

    /// <summary>
    /// Whether more matches follow the returned ones; possible only when a limit was set.
    /// </summary>
    public bool HasMore { get; }

    /// <summary>
    /// The position this answer is based on: the node had applied everything up to it when
    /// it searched the index.
    /// </summary>
    public LatticePosition Position { get; }
}
