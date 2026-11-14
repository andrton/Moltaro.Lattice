using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice;

/// <summary>
/// One database instance: its schema and entities. Create and open it with
/// <see cref="LatticeDatabase"/>; close it with <see cref="CloseAsync"/> or <c>await using</c>.
/// </summary>
/// <remarks>
/// An instance is opened once and closed once. All members are safe to call from several
/// threads. To recover from <see cref="LatticeDatabaseStateEnum.Faulted"/> or to use the
/// database after closing, create a new instance.
/// </remarks>
public interface ILatticeDatabase : IAsyncDisposable, IDisposable
{
    /// <summary>The current lifecycle state.</summary>
    LatticeDatabaseStateEnum State { get; }

    /// <summary>
    /// The schema of the database. A child has the schema of its master.
    /// </summary>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.Unavailable"/>: the database is not ready.
    /// </exception>
    LatticeSchema Schema { get; }

    /// <summary>
    /// The position this instance has reached: on a master the last write it has published,
    /// on a child the last write it has applied.
    /// </summary>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.Unavailable"/>: the database is not ready.
    /// </exception>
    LatticePosition Position { get; }

    /// <summary>
    /// Waits until this instance has applied the given position: all writes up to it, with
    /// their index changes, are then readable here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use it to admit a request once and then read several times, for example through
    /// indexes. If the instance is already there, the call completes at once. It does not
    /// contact the master, does not wait for other nodes and does not follow newer writes:
    /// the target is the position given.
    /// </para>
    /// <para>
    /// Success is a lower bound, not a snapshot: later writes may already be visible, and
    /// nothing is locked. To keep the guarantee if the instance reloads in between, pass
    /// the same position, through a <see cref="LatticeSession"/>, to the reads that follow.
    /// </para>
    /// </remarks>
    /// <param name="position">The position to wait for.</param>
    /// <param name="timeout">
    /// How long to wait; <see langword="null"/> for
    /// <see cref="LatticeDatabaseOptions.SynchronizationTimeout"/>. Always finite.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, nothing else.</param>
    /// <exception cref="ArgumentException">The position is the default value.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/>: the instance did not reach the
    /// position in time.
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/>: the position belongs to another
    /// database history.
    /// <see cref="LatticeErrorEnum.Unavailable"/>: the database is not ready.
    /// </exception>
    ValueTask WaitForPositionAsync(LatticePosition position, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits until this instance has applied the position the session depends on, with the
    /// session's timeout. Completes at once for a session without a position. See
    /// <see cref="WaitForPositionAsync(LatticePosition, TimeSpan?, CancellationToken)"/>.
    /// </summary>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the wait, nothing else.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/>,
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/> or
    /// <see cref="LatticeErrorEnum.Unavailable"/>, as for a position.
    /// </exception>
    ValueTask WaitForPositionAsync(LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the database and completes when it is <see cref="LatticeDatabaseStateEnum.Ready"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A master starts with the configured schema. With
    /// <see cref="LatticeDatabaseOptions.ListenUri"/> it also accepts children at that address.
    /// </para>
    /// <para>
    /// A child connects to the master at <see cref="LatticeDatabaseOptions.MasterUri"/>, loads
    /// the whole database and only then becomes readable. While the master is unreachable the
    /// call keeps waiting until it is cancelled or the database is closed.
    /// </para>
    /// <para>
    /// A master and its children can be opened in one process. Cancelling or failing the
    /// call closes the instance.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels opening.</param>
    /// <exception cref="InvalidOperationException">The database has already been opened.</exception>
    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the entities of the entity type that the schema declares for the .NET class
    /// <typeparamref name="TEntity"/>. The entity type, its key and its fields are taken
    /// from the schema; the returned object is cached and safe to share between threads.
    /// </summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema declares no entity type for
    /// the class, the class does not have the properties the entity type declares, or a
    /// property is a .NET enum that does not agree with the enum type of its field, see
    /// <see cref="LatticeValueDeclaration.ValueType"/>.
    /// <see cref="LatticeErrorEnum.Unavailable"/>: the database is not ready.
    /// </exception>
    ILatticeSet<TEntity> Set<TEntity>()
        where TEntity : class;

    /// <summary>
    /// Returns the entities of an entity type by its name, as records. Works for every
    /// entity type of the schema and needs no .NET class.
    /// </summary>
    /// <param name="entityName">The entity type name declared in the schema.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema has no such entity type.
    /// <see cref="LatticeErrorEnum.Unavailable"/>: the database is not ready.
    /// </exception>
    ILatticeSet Set(string entityName);

    /// <summary>
    /// Starts a write batch: several writes, possibly of different entity types, that the
    /// database executes together or not at all. Creating a batch and adding to it touches
    /// nothing in the database; see <see cref="ILatticeWriteBatch"/>.
    /// </summary>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.Unavailable"/>: the database is not ready.
    /// </exception>
    ILatticeWriteBatch CreateWriteBatch();

    /// <summary>
    /// Closes the database: stops accepting operations, lets started writes reach their
    /// outcome and releases the memory. Closing a closed database does nothing.
    /// </summary>
    /// <remarks>
    /// Entities returned by earlier reads stay valid. A MemoryOnly database saves nothing:
    /// its data is gone after closing. A child whose master has closed keeps serving its
    /// local reads. Disposing the database closes it in the same way.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Cancels waiting for the close to finish. The database still closes.
    /// </param>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
