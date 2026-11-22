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
/// not a snapshot: entities written while it runs may or may not be included. A search
/// returns an entity at most once, also one that is deleted and created again while it runs
/// or whose indexed values move it ahead of the search, and every entity that keeps its
/// indexed values for the whole search, in the order of the index or its reverse.
/// </para>
/// <para>
/// The order is the order of the index: by its first field, then by its second, and so on,
/// with the entities without a value in a field after those with one. In reverse it is the
/// exact mirror of that order, so the entities without a value come first there. Entities
/// with the same indexed values come in no particular order among themselves.
/// </para>
/// <para>
/// As with the other reads there are two forms. <see cref="Find"/> answers at once from
/// what this node has. <see cref="FindAsync"/> can first wait, through the session in its
/// options, until this node has applied a position.
/// </para>
/// <para>
/// The object is safe to keep and to share between threads.
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
    /// The search is not a snapshot: an entity written into the key while the matches are
    /// enumerated may be missing from them, even when that write completed before the
    /// enumeration ended.
    /// </para>
    /// <para>
    /// Of an entity type whose writes are refused until a feature of its schema is implemented,
    /// such as an owned value, a collection or a reference, the search finds
    /// nothing, as a read of it by key finds nothing and an enumeration of it is empty: such a
    /// type holds no entity.
    /// </para>
    /// </remarks>
    /// <param name="key">What to look for.</param>
    /// <exception cref="ArgumentException">
    /// The index cannot serve the key, or the key is the default of <see cref="IndexKey"/>, which
    /// is no key.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ReadLimitExceeded"/>: the step would have exceeded a read
    /// limit of the options, which <see cref="LatticeException.Limit"/> names, as
    /// <see cref="LatticeDatabaseOptions.ReadMemoryLimit"/> and
    /// <see cref="LatticeDatabaseOptions.ReadTimeLimit"/> state. The entities returned before stay
    /// valid; every further step fails with the same error.
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
    /// Of an entity type whose writes are refused until a feature of its schema is implemented,
    /// the result has no item, as a read by key finds nothing: such a type holds no entity.
    /// </para>
    /// </remarks>
    /// <param name="key">What to look for.</param>
    /// <param name="options">Consistency, direction and paging of the search.</param>
    /// <param name="cancellationToken">Cancels the wait for the session's position and the search.</param>
    /// <returns>The matches and the position of the answer.</returns>
    /// <exception cref="ArgumentNullException">
    /// The options are <see langword="null"/>; thrown by the call.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The index cannot serve the key, or the key is the default of <see cref="IndexKey"/>, which
    /// is no key; thrown by the call.
    /// </exception>
    /// <exception cref="LatticeException">
    /// Through the returned task, also when the search needs no wait:
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/>: this node did not reach the
    /// position of the session in time; nothing was searched.
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/>: the session belongs to another
    /// database history.
    /// <see cref="LatticeErrorEnum.ReadLimitExceeded"/>: the search would have exceeded a read
    /// limit of the options, which <see cref="LatticeException.Limit"/> names; it returns nothing.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token was cancelled, before a read limit is looked at; through the returned task.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// The database was closed; through the returned task.
    /// </exception>
    ValueTask<LatticeFindResult<TEntity>> FindAsync(IndexKey key, LatticeFindOptions options, CancellationToken cancellationToken = default);
}
