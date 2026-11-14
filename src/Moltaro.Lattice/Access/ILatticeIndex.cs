using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// One secondary index of an entity type, selected by its name in the schema: finds
/// entities by the values of the indexed fields. Obtained from
/// <see cref="ILatticeSet{TEntity}.Index"/> or <see cref="ILatticeSet.Index"/>; the same
/// interface serves entity classes and records.
/// </summary>
/// <remarks>
/// <para>
/// The application chooses the index; nothing is chosen for it and nothing falls back to
/// reading every entity. An index answers what its declaration allows, see
/// <see cref="LatticeIndex"/> and <see cref="IndexKey"/>; anything else is an
/// <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// An index is always in step with the entities on the same node: a write and its index
/// changes become visible together. Like every read, a search returns owned copies and is
/// not a snapshot: entities written while it runs may or may not be included.
/// </para>
/// <para>
/// As with the other reads there are two forms. <see cref="Find"/> answers at once from
/// what this node has. <see cref="FindAsync"/> can first wait, through the session in its
/// options, until this node has applied a position.
/// </para>
/// <para>
/// The object is safe to keep and to share between threads.
/// </para>
/// <para>
/// <b>Not implemented yet.</b> An index is resolved and a key is checked against it, but
/// <see cref="Find"/> and <see cref="FindAsync"/> then throw <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity class, or <see cref="LatticeRecord"/>.</typeparam>
public interface ILatticeIndex<TEntity>
    where TEntity : class
{
    /// <summary>The index as declared in the schema.</summary>
    LatticeIndex Definition { get; }

    /// <summary>
    /// Finds the entities that match the key in what this node has now, in the index order.
    /// The matches are produced as they are enumerated.
    /// </summary>
    /// <param name="key">What to look for.</param>
    /// <exception cref="ArgumentException">The index cannot serve the key.</exception>
    IEnumerable<TEntity> Find(IndexKey key);

    /// <summary>
    /// Finds the entities that match the key, as the options say: after the position of a
    /// session, in the index order or its reverse, all of them or a part.
    /// </summary>
    /// <param name="key">What to look for.</param>
    /// <param name="options">Consistency, direction and paging of the search.</param>
    /// <param name="cancellationToken">Cancels the wait for the session's position.</param>
    /// <returns>The matches and the position of the answer.</returns>
    /// <exception cref="ArgumentException">The index cannot serve the key.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/>: this node did not reach the
    /// position of the session in time; nothing was searched.
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/>: the session belongs to another
    /// database history.
    /// </exception>
    ValueTask<LatticeFindResult<TEntity>> FindAsync(IndexKey key, LatticeFindOptions options, CancellationToken cancellationToken = default);
}
