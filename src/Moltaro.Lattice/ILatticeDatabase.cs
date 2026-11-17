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
    /// The address at which this master accepts its children, with the port it actually listens
    /// at: when <see cref="LatticeDatabaseOptions.ListenUri"/> names port 0, the port the system
    /// chose. <see langword="null"/> for a master opened without
    /// <see cref="LatticeDatabaseOptions.ListenUri"/> and for a child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A child of this master is given this address as <see cref="LatticeDatabaseOptions.MasterUri"/>.
    /// It does not change after the open.
    /// </para>
    /// <para>
    /// <b>Not implemented yet.</b> No instance listens on the network: the database that
    /// <see cref="LatticeDatabase"/> opens today returns the configured address as it is.
    /// </para>
    /// </remarks>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.Unavailable"/>: the database is not ready.
    /// </exception>
    Uri? ListenUri { get; }

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
    /// <see cref="LatticeDatabaseOptions.ListenUri"/> it also accepts children at that address,
    /// and <see cref="ListenUri"/> reports the port it listens at.
    /// </para>
    /// <para>
    /// A child connects to the master at <see cref="LatticeDatabaseOptions.MasterUri"/>, loads
    /// the whole database and only then becomes readable. When it is not ready within
    /// <see cref="LatticeDatabaseOptions.OpenTimeout"/>, the call fails with
    /// <see cref="LatticeErrorEnum.Unavailable"/> and the instance is closed.
    /// </para>
    /// <para>
    /// A master and its children can be opened in one process. Cancelling or failing the
    /// call closes the instance; a cancelled call ends in <see cref="OperationCanceledException"/>.
    /// An open that fails, times out or is cancelled leaves nothing of the instance behind: no
    /// connection, no work that still runs and no memory it holds.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">
    /// Cancels opening. Every wait of the open honors it, a child's attempts to reach its master
    /// and its waits for the data of the load included.
    /// </param>
    /// <exception cref="InvalidOperationException">The database has already been opened.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.Unavailable"/>: a child is not ready within
    /// <see cref="LatticeDatabaseOptions.OpenTimeout"/>, or a failure that another attempt cannot
    /// mend ended its open at once, as <see cref="LatticeDatabaseOptions.OpenTimeout"/> says; the
    /// message names the reason, and the instance is closed.
    /// </exception>
    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the entities of the entity type that the schema declares for the .NET class
    /// <typeparamref name="TEntity"/>. The entity type, its key and its fields are taken
    /// from the schema; the returned object is cached and safe to share between threads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The class is bound to its entity type here, once: every field of the entity type is
    /// matched with a property of the class, and the class is not checked again when an
    /// entity is read or written. The property of a field is the one C# finds by the name of
    /// the field on the class: the public member of that name of the most derived class that
    /// declares one. It has to be an instance property of the type the field needs.
    /// A property that hides one of its base class with that type binds, and the field is
    /// read from and written to it.
    /// </para>
    /// <para>
    /// The property of a field has a public get accessor and a set accessor of any
    /// accessibility: public, <c>internal</c>, <c>protected</c>, <c>private</c> or
    /// <c>init</c>. The database sets the value through that accessor when it builds an
    /// object, so a class may keep its properties closed for writing by the application. The
    /// accessors are those C# sees for the property on the class, inherited ones included: a
    /// class that overrides only the get accessor of a virtual property is set through the
    /// accessor of its base class. When the public member C# finds on the class by the name of
    /// a field is not a property — a public field that hides a property of a base class — the
    /// class is refused: the hidden property is not bound in its place.
    /// </para>
    /// <para>
    /// The type a field needs follows from its declaration, by one rule for every position: a
    /// property, and an element, a key or a value of a collection alike.
    /// </para>
    /// <list type="table">
    /// <listheader>
    /// <term>Declared</term>
    /// <description>In a bound class</description>
    /// </listheader>
    /// <item>
    /// <term>A scalar or <see cref="LatticeFieldTypeEnum.Binary"/></term>
    /// <description>Its <see cref="LatticeValueDeclaration.ValueType"/>.</description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Enum"/></term>
    /// <description>The underlying integer type, or a .NET enum that agrees with the enum type.</description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Owned"/></term>
    /// <description>The class named by <see cref="LatticeOwnedType.ClrTypeName"/> of the owned type.</description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Reference"/></term>
    /// <description>The key type of the target.</description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.List"/> of T</term>
    /// <description><c>List&lt;T&gt;</c> or <c>T[]</c>.</description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Map"/> of K, V</term>
    /// <description><c>Dictionary&lt;K, V&gt;</c>.</description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Set"/> of T</term>
    /// <description><c>HashSet&lt;T&gt;</c>.</description>
    /// </item>
    /// </list>
    /// <para>
    /// T, K and V are found by the same table, and a nullable position whose type is a value
    /// type is <see cref="Nullable{T}"/> of that type. A List of owned values is therefore a
    /// <c>List&lt;T&gt;</c> or an array of the class of the owned type, and a Map of them a
    /// <c>Dictionary</c> with that class as its value.
    /// </para>
    /// <para>
    /// An Enum declared as its underlying integer holds the value as it is stored, and binding
    /// does not compare it with the members of the enum type; a write checks the value, as it
    /// checks the value of a .NET enum, by <see cref="LatticeEnumType.IsValid"/>. A .NET enum,
    /// as a property or at a position inside a collection, has to agree with the enum type: the
    /// same underlying type, the same members, every name with its value in both directions, and
    /// <see cref="FlagsAttribute"/> exactly when the enum type is a Flags type. A .NET enum
    /// with two names for one value therefore agrees only with an enum type that declares both
    /// names. Nothing is mapped or guessed.
    /// </para>
    /// <para>
    /// The class of an owned type is bound as the class of an entity type is: it is not
    /// abstract and has a public parameterless constructor, it has for every field of the
    /// owned type a property found and set by the same rules, and its own owned values and
    /// collections follow the table. An owned type that names no <c>ClrType</c> has no form
    /// in a class, and neither has <see cref="LatticeOwnedValue"/>, which only a record holds.
    /// </para>
    /// <para>
    /// A class that does not agree with the schema is found here, and not later when a value
    /// is read or written. Every way it can fail is <see cref="LatticeErrorEnum.BindingFailed"/>;
    /// none ends in another exception. The members of <see cref="ILatticeWriteBatch"/> that
    /// take an entity class bind it in the same way.
    /// </para>
    /// </remarks>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema declares no entity type for
    /// the class; the class, or the class of an owned type it holds, is abstract or has no
    /// public parameterless constructor; the member that C# finds by the name of a field, in
    /// the class or in the class of an owned type, is missing or is not a property, such as a
    /// public field that hides a property of a base class; that property is static, an
    /// indexer, without a public get accessor or without a set accessor, or of another type
    /// than the table gives, a .NET enum that does not agree with its enum type included; a
    /// field at any position is of an owned type that names no <c>ClrType</c>; or a property,
    /// an element or a value is a <see cref="LatticeOwnedValue"/>. The message names the
    /// class and, where one is concerned, the property or the member that stands in its
    /// place; for the last two causes it names the owned type as well.
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
    /// local reads. A master also stops accepting children and ends every connection to them,
    /// and its close completes only once no work of the instance runs any more and its port is
    /// free: another instance can listen at the address as soon as the close has completed.
    /// Disposing the database closes it in the same way. A call made while an
    /// earlier close is in progress, and a dispose, starts no other close: it completes when
    /// that close completes, once the memory is released; the token of a call cancels the wait
    /// of that call alone. The synchronous <see cref="IDisposable.Dispose"/> blocks its thread
    /// until the close has completed.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Cancels waiting for the close to finish. The database still closes.
    /// </param>
    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
