namespace Moltaro.Lattice;

/// <summary>
/// Reason of a <see cref="LatticeException"/>.
/// </summary>
public enum LatticeErrorEnum
{
    /// <summary>The database options are missing a required value or combine incompatible ones.</summary>
    InvalidConfiguration = 1,

    /// <summary>The schema descriptor or the compiled schema is not valid.</summary>
    InvalidSchema = 10,

    /// <summary>
    /// The requested entity type does not exist in the schema, or the .NET class does not
    /// match its declaration by the rules that <see cref="ILatticeDatabase.Set{TEntity}"/>
    /// states.
    /// </summary>
    BindingFailed = 2,

    /// <summary>The submitted entity violates the schema. Nothing was written.</summary>
    /// <remarks>
    /// Text that is not well-formed — a surrogate without its pair — is refused in the same
    /// way, at any depth, and is never stored and never replaced by another character. A
    /// String or Binary value of a field of the entity that is larger than one value may be, as
    /// <see cref="Schema.LatticeFieldTypeEnum.String"/> and
    /// <see cref="Schema.LatticeFieldTypeEnum.Binary"/> state, is refused in the same way. An
    /// integer that the enum type of its field does not declare — for a Flags type, a bit that no
    /// member declares — is refused in the same way.
    /// </remarks>
    ValidationFailed = 3,

    /// <summary>Insert found an existing entity with the same primary key. Nothing was written.</summary>
    DuplicateKey = 4,

    /// <summary>Replace found no entity with the given primary key. Nothing was written.</summary>
    NotFound = 5,

    /// <summary>
    /// The entity changed or disappeared since the expected revision was read. Nothing was
    /// written; the application decides whether to read again and resubmit.
    /// </summary>
    RevisionConflict = 6,

    /// <summary>
    /// The write was refused before execution because the instance cannot hold it: within its
    /// memory budget, or within a limit of its storage that is set, such as the limit of its
    /// journal. It was not executed and will not execute later.
    /// <see cref="LatticeException.Limit"/> names the limit: <see cref="LatticeLimitEnum.Memory"/>
    /// for the memory budget, <see cref="LatticeDatabaseOptions.MemoryLimit"/>.
    /// </summary>
    WriteRejected = 7,

    /// <summary>
    /// The outcome of the write is unknown: it may or may not have been committed. Losing the
    /// response does not prove a rollback, so the write must not be blindly repeated.
    /// </summary>
    /// <remarks>
    /// A write is reported so as well when the instance became
    /// <see cref="LatticeDatabaseStateEnum.Faulted"/> during its publication, or during the
    /// publication of a write taken for storage together with it and before it: storage took the
    /// write, and the instance cannot show it. A write through a child is reported so as well
    /// when the child had sent the whole write to its master and the connection ended, or the
    /// child was closed, before the reply arrived: the master may have executed it.
    /// </remarks>
    IndeterminateOutcome = 8,

    /// <summary>
    /// The instance cannot serve the operation in its current state: it is not ready yet,
    /// is reloading or has faulted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A write through a child is reported so as well when the child has no connection to its
    /// master, when the connection ended or the child was closed before the child had sent the
    /// whole write, and when a master that was closing did not execute it. In each case the
    /// write was not executed.
    /// </para>
    /// <para>
    /// <b>Not implemented yet.</b> A child whose connection to its master has ended does not
    /// connect again: it goes on serving its reads, and every later write through it is reported
    /// so.
    /// </para>
    /// </remarks>
    Unavailable = 9,

    /// <summary>
    /// The node did not reach the required position within the synchronization timeout.
    /// Nothing stale was returned. When <see cref="LatticeException.CommittedPosition"/> is
    /// set, the write itself is committed and only its arrival on this node is late.
    /// </summary>
    SynchronizationTimeout = 11,

    /// <summary>
    /// The required position belongs to another database history, for example to the
    /// database as it was before a restore. Positions of different histories are never
    /// compared; start a new session.
    /// </summary>
    HistoryMismatch = 12,

    /// <summary>
    /// The write would give two entities the same value in a unique index. Nothing was
    /// written; a write batch is refused as a whole. <see cref="LatticeException.IndexName"/>
    /// names the index.
    /// </summary>
    /// <remarks>
    /// <b>Not implemented yet.</b> Unique indexes are not implemented; what a write does
    /// until they are is stated at <see cref="Schema.LatticeIndex.IsUnique"/>.
    /// </remarks>
    UniqueConstraintViolation = 13,

    /// <summary>
    /// A read would have exceeded a limit of the database options: of the temporary memory it
    /// may use, <see cref="LatticeDatabaseOptions.ReadMemoryLimit"/>, or of the time it may
    /// spend inside the engine, <see cref="LatticeDatabaseOptions.ReadTimeLimit"/>.
    /// <see cref="LatticeException.Limit"/> names the limit. What the read returned before
    /// stays valid, but the read did not complete and never ends as if it had.
    /// </summary>
    ReadLimitExceeded = 14,
}
