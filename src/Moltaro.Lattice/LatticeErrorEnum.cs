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
    /// String or Binary value that is larger than one value may be, as
    /// <see cref="Schema.LatticeFieldTypeEnum.String"/> and
    /// <see cref="Schema.LatticeFieldTypeEnum.Binary"/> state, in a field of the entity, a field
    /// of an owned value or an element of a List, is refused in the same way; and so is a List, at
    /// any depth, of more elements than <see cref="Schema.LatticeFieldTypeEnum.List"/> states, for
    /// an entity type the engine stores. An
    /// integer that the enum type of its field does not declare — for a Flags type, a bit that no
    /// member declares — is refused in the same way.
    /// </remarks>
    ValidationFailed = 3,

    /// <summary>Insert found an existing entity with the same primary key. Nothing was written.</summary>
    DuplicateKey = 4,

    /// <summary>A replacement or partial update found no entity with the given primary key. Nothing was written.</summary>
    NotFound = 5,

    /// <summary>
    /// The entity changed or disappeared since the expected revision was read. Nothing was
    /// written; the application decides whether to read again and resubmit.
    /// </summary>
    RevisionConflict = 6,

    /// <summary>
    /// The write was refused before commit because the instance cannot hold it. It has no
    /// effect and will never run later. <see cref="LatticeException.Limit"/> names the exhausted
    /// resource, such as <see cref="LatticeLimitEnum.Memory"/> for the instance memory budget
    /// or <see cref="LatticeLimitEnum.RootSlots"/> for the addresses of entity lifetimes.
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
    /// An operation in flight on an instance that is being closed, which the close does not let
    /// finish, is reported so as well: a wait for a position, and on a child a write its master
    /// has not taken. A started write of a master reaches its outcome. An
    /// <see cref="ILatticeDatabase.OpenAsync"/> that still waits is cancelled instead, as it
    /// states.
    /// </para>
    /// <para>
    /// A write through a child is reported so as well when the child has no connection to its
    /// master, when the connection ended or the child was closed before the child had sent the
    /// whole write, and when a master that was closing did not execute it. In each case the
    /// write was not executed.
    /// </para>
    /// <para>
    /// A child whose memory budget or root slots cannot hold its initial load reports this
    /// error from <see cref="ILatticeDatabase.OpenAsync"/> and closes before it becomes ready.
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
    /// set, the master has committed the write, and only its arrival on this node is late.
    /// </summary>
    /// <remarks>
    /// On a child that has lost its master the wait ends at once, because the position cannot
    /// arrive; so does the wait of a write through a child that has faulted after its master
    /// committed the write. A write through the child that has its reply and waits for the
    /// child to apply it ends so when the connection ends or the child faults, with
    /// <see cref="LatticeException.CommittedPosition"/> set: the write is committed and must not
    /// be repeated.
    /// </remarks>
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
    /// names the index; <see cref="Schema.LatticeIndex.IsUnique"/> states the rule.
    /// </summary>
    UniqueConstraintViolation = 13,

    /// <summary>
    /// A read would have exceeded a limit of the database options: of the temporary memory it
    /// may use, <see cref="LatticeDatabaseOptions.ReadMemoryLimit"/>, or of the time it may
    /// spend inside the engine, <see cref="LatticeDatabaseOptions.ReadTimeLimit"/>.
    /// <see cref="LatticeException.Limit"/> names the limit. What the read returned before
    /// stays valid, but the read did not complete and never ends as if it had.
    /// </summary>
    ReadLimitExceeded = 14,

    /// <summary>
    /// A partial update encountered an absent owned parent, a null collection for an element
    /// command or a List position outside the range of its command. Nothing was written.
    /// </summary>
    UpdateTargetMissing = 15,
}
