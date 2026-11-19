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
/// <b>Not implemented yet:</b> a search by the leading fields of an index, with a range, in
/// reverse, or with <see cref="LatticeFindOptions.Offset"/> or <see cref="LatticeFindOptions.Limit"/>.
/// Such a key or such options are checked against the index as any are, and then refused with
/// <see cref="NotSupportedException"/>. A search gives a value to every field of its index and
/// returns every entity with that key.
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
    /// <remarks>
    /// <para>
    /// An entity that is deleted and created again while the enumeration runs may be returned
    /// twice; returning every entity at most once is not implemented yet.
    /// </para>
    /// <para>
    /// Of an entity type whose writes are refused until a feature of its schema is implemented,
    /// such as an owned value, a collection, a reference or a unique index, the search finds
    /// nothing, as a read of it by key finds nothing and an enumeration of it is empty: such a
    /// type holds no entity.
    /// </para>
    /// </remarks>
    /// <param name="key">What to look for.</param>
    /// <exception cref="ArgumentException">
    /// The index cannot serve the key, or the key is the default of <see cref="IndexKey"/>, which
    /// is no key.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The key gives values to the leading fields only, or has a range, which is not implemented
    /// yet.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The database was closed: thrown by the step of the enumeration that meets the close, and by
    /// every step after it.
    /// </exception>
    IEnumerable<TEntity> Find(IndexKey key);

    /// <summary>
    /// Finds the entities that match the key, as the options say: after the position of a
    /// session, in the index order or its reverse, all of them or a part.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An entity that is deleted and created again while the search runs may be in the result
    /// twice; returning every entity at most once is not implemented yet.
    /// </para>
    /// <para>
    /// Of an entity type whose writes are refused until a feature of its schema is implemented,
    /// the result has no item, as a read by key finds nothing: such a type holds no entity.
    /// </para>
    /// </remarks>
    /// <param name="key">What to look for.</param>
    /// <param name="options">Consistency, direction and paging of the search.</param>
    /// <param name="cancellationToken">Cancels the wait for the session's position.</param>
    /// <returns>The matches and the position of the answer.</returns>
    /// <exception cref="ArgumentException">
    /// The index cannot serve the key, or the key is the default of <see cref="IndexKey"/>, which
    /// is no key.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/>: this node did not reach the
    /// position of the session in time; nothing was searched.
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/>: the session belongs to another
    /// database history.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The key gives values to the leading fields only or has a range, or the options ask for the
    /// reverse order, an offset or a limit, which are not implemented yet.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The database was closed.</exception>
    ValueTask<LatticeFindResult<TEntity>> FindAsync(IndexKey key, LatticeFindOptions options, CancellationToken cancellationToken = default);
}
