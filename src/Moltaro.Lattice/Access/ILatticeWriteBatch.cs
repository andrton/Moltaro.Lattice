namespace Moltaro.Lattice.Access;

/// <summary>
/// Several writes that the database executes together or not at all. Obtained from
/// <see cref="ILatticeDatabase.CreateWriteBatch"/>, filled with operations and submitted
/// once with <see cref="CommitAsync(CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a batch guarantees.</b> The master executes all operations of a batch as one
/// change at one position, or none of them, and a child applies it as a whole. No reader, on
/// the master or on a child, sees an entity of the batch in its new state before that
/// position on that node, and a read that begins once its node has reached that position, as
/// a read with the session of the commit does, sees all of the batch. A search
/// or an enumeration that runs while the batch is published is no snapshot: it may return
/// some entities of the batch as they were before it and others as after, as separate reads
/// may. The operations may address different entity types of the same database.
/// </para>
/// <para>
/// <b>What a batch is not.</b> The batch is one transaction of the database, but not a
/// transaction the application works inside: it cannot read, it holds no lock while the
/// application builds it, and it does not span two databases or an external one. A
/// condition on the current state is expressed by the operation itself. Insert requires
/// an absent key. Replace and Update require an existing entity. An expected revision
/// requires that the entity has not changed since it was read.
/// </para>
/// <para>
/// <b>Building.</b> Adding an operation to a batch of an open database contacts nothing. The
/// entity or record is copied when it is added, so later edits of the object are not part of
/// the batch. Adding a partial update validates its copied content synchronously before
/// checking whether the batch was submitted, whether the entity is already addressed and
/// whether the database has closed. An invalid input or a duplicate operation therefore
/// has the same error on an open or closed database. Once these checks pass, adding any
/// operation to a closed database throws <see cref="ObjectDisposedException"/>. A batch
/// takes one operation per entity. Two operations address the same entity when they name the
/// same entity type and their primary keys are equal as the schema compares them: an integer
/// key by its value whatever the width it was given in, a String key under the comparison
/// declared for the key field. Adding a second operation for the same entity is an
/// <see cref="ArgumentException"/>, and the batch stays as it was. An entity that violates the
/// schema is refused when it is added, with
/// <see cref="LatticeErrorEnum.ValidationFailed"/>. Text that is not well-formed — a
/// surrogate without its pair — is refused in the same way, at any depth, and is never stored
/// and never replaced by another character. A String or Binary value that is larger than one
/// value may be, as <see cref="Schema.LatticeFieldTypeEnum.String"/> and
/// <see cref="Schema.LatticeFieldTypeEnum.Binary"/> state, is refused at any depth, including
/// in a collection. A collection above its entry limit is refused in the same way, as
/// <see cref="Schema.LatticeFieldTypeEnum.List"/>, <see cref="Schema.LatticeFieldTypeEnum.Map"/>
/// and <see cref="Schema.LatticeFieldTypeEnum.Set"/> state. An integer
/// that the enum type of its field does not declare — for a Flags type, a bit that no member
/// declares — is refused in the same way. A key
/// given to a delete that is a String which is not well-formed text is an
/// <see cref="ArgumentException"/>, as a key of another type is. A batch is built by one
/// caller and is not safe to use from several threads at once.
/// </para>
/// <para>
/// On either role, adding an operation reserves local memory for its input after its input is
/// copied. The call reports <see cref="LatticeErrorEnum.WriteRejected"/> with
/// <see cref="LatticeLimitEnum.Memory"/> and no <see cref="LatticeException.OperationIndex"/>
/// when that reservation cannot fit, before contacting the master. Every check of the input is
/// made first and keeps its usual code, so a refusal for memory never comes before one of those
/// errors. Earlier operations, the count and any update passed to the call are preserved. The
/// caller can retry the addition when memory is available.
/// Submitting the batch transfers its charged input to the database until that input can be
/// released. A batch that still waits for its turn takes no new preparation reservation.
/// </para>
/// <para>
/// <b>Executing.</b> The master checks every operation against the state at the moment of
/// execution, and the unique indexes against the state the batch would leave. Two
/// entities may therefore exchange their values of a unique index inside one batch. When
/// any check fails, the whole batch is refused and nothing is written; the exception
/// carries the error of the failed operation and, in
/// <see cref="LatticeException.OperationIndex"/>, its number. A batch has no limit of its
/// own. The master refuses a batch it cannot hold within an instance resource, such as memory
/// or addressable root slots, with <see cref="LatticeErrorEnum.WriteRejected"/> before anything
/// is written. A refusal for either resource leaves <see cref="LatticeException.OperationIndex"/>
/// unset.
/// </para>
/// <para>
/// A batch submitted through a child is executed by the master and completes when that
/// child has applied it. When the child does not apply it within the synchronization
/// timeout, the commit fails with <see cref="LatticeErrorEnum.SynchronizationTimeout"/>
/// and <see cref="LatticeException.CommittedPosition"/> set: the batch is committed, is
/// not rolled back and must not be submitted again. If the child closes after receiving
/// the master's reply, the call returns the master's result. The result of each update
/// command is available only after a successful commit, including that result on close.
/// </para>
/// </remarks>
public interface ILatticeWriteBatch
{
    /// <summary>The number of operations added so far.</summary>
    int Count { get; }

    /// <summary>Adds the creation of a new entity. The primary key is taken from the entity.</summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <param name="entity">The complete entity.</param>
    /// <exception cref="ArgumentException">The batch already has an operation for this entity.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the class cannot be bound to an entity
    /// type of the schema, as for <see cref="ILatticeDatabase.Set{TEntity}"/>.
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>: the entity violates the schema.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Insert<TEntity>(TEntity entity)
        where TEntity : class;

    /// <summary>Adds the creation of a new entity from a record.</summary>
    /// <param name="record">The complete record, of an entity type of this database.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the record belongs to another schema.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>: the record violates the schema.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Insert(LatticeRecord record);

    /// <summary>Adds the replacement of an existing entity as a whole.</summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <param name="entity">The complete new state of the entity.</param>
    /// <exception cref="ArgumentException">The batch already has an operation for this entity.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/> or
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>, as for <see cref="Insert{TEntity}"/>.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Replace<TEntity>(TEntity entity)
        where TEntity : class;

    /// <summary>
    /// Adds the replacement of an existing entity as a whole, provided it still has the
    /// expected revision when the batch is executed.
    /// </summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <param name="entity">The complete new state of the entity.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <exception cref="ArgumentException">The batch already has an operation for this entity.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/> or
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>, as for <see cref="Insert{TEntity}"/>.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Replace<TEntity>(TEntity entity, LatticeRevision expectedRevision)
        where TEntity : class;

    /// <summary>Adds the replacement of an existing entity as a whole, from a record.</summary>
    /// <param name="record">The complete new state, of an entity type of this database.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the record belongs to another schema.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>: the record violates the schema.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Replace(LatticeRecord record);

    /// <summary>
    /// Adds the replacement of an existing entity as a whole, from a record, provided the
    /// entity still has the expected revision when the batch is executed.
    /// </summary>
    /// <param name="record">The complete new state, of an entity type of this database.</param>
    /// <param name="expectedRevision">The revision read together with the record.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the record belongs to another schema.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>: the record violates the schema.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Replace(LatticeRecord record, LatticeRevision expectedRevision);

    /// <summary>
    /// Adds a save of the entity: created when its key is absent, otherwise replaced as a
    /// whole. This is the ordinary way to save several entities together.
    /// </summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <param name="entity">The complete entity.</param>
    /// <exception cref="ArgumentException">The batch already has an operation for this entity.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/> or
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>, as for <see cref="Insert{TEntity}"/>.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Upsert<TEntity>(TEntity entity)
        where TEntity : class;

    /// <summary>
    /// Adds a save of the record: created when its key is absent, otherwise replaced as a whole.
    /// </summary>
    /// <param name="record">The complete record, of an entity type of this database.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the record belongs to another schema.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>: the record violates the schema.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Upsert(LatticeRecord record);

    /// <summary>
    /// Adds the deletion of the entity with the given primary key. A key that is absent
    /// when the batch is executed is not a failure; that operation changes nothing.
    /// </summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <param name="key">The primary key, of the key type declared in the schema.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the key has another type or is
    /// text that is not well-formed.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the class cannot be bound to an entity
    /// type of the schema, as for <see cref="ILatticeDatabase.Set{TEntity}"/>.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Delete<TEntity>(object key)
        where TEntity : class;

    /// <summary>
    /// Adds the deletion of the entity with the given primary key, provided it still has
    /// the expected revision when the batch is executed.
    /// </summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <param name="key">The primary key, of the key type declared in the schema.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the key has another type or is
    /// text that is not well-formed.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the class cannot be bound to an entity
    /// type of the schema, as for <see cref="ILatticeDatabase.Set{TEntity}"/>.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Delete<TEntity>(object key, LatticeRevision expectedRevision)
        where TEntity : class;

    /// <summary>
    /// Adds the deletion of the entity of the named entity type with the given primary
    /// key. A key that is absent when the batch is executed is not a failure.
    /// </summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="entityName">The entity type name declared in the schema.</param>
    /// <param name="key">The primary key, of the key type declared in the schema.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the key has another type or is
    /// text that is not well-formed.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema has no such entity type.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Delete<TKey>(string entityName, TKey key)
        where TKey : notnull;

    /// <summary>
    /// Adds the deletion of the entity of the named entity type with the given primary
    /// key, provided it still has the expected revision when the batch is executed.
    /// </summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="entityName">The entity type name declared in the schema.</param>
    /// <param name="key">The primary key, of the key type declared in the schema.</param>
    /// <param name="expectedRevision">The revision read together with the record.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the key has another type or is
    /// text that is not well-formed.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema has no such entity type.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="InvalidOperationException">The batch has already been submitted.</exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Delete<TKey>(string entityName, TKey key, LatticeRevision expectedRevision)
        where TKey : notnull;

    /// <summary>
    /// Adds a partial update for an existing entity. Success consumes the update.
    /// A refusal leaves both builders as they were.
    /// </summary>
    /// <param name="update">An unsubmitted update made for this database instance.</param>
    /// <exception cref="ArgumentNullException">The update is null.</exception>
    /// <exception cref="ArgumentException">The update belongs to another database instance or the batch already addresses this entity.</exception>
    /// <exception cref="InvalidOperationException">The update is empty, or the update or batch is already submitted.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/> when copied operands violate the schema.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Update(LatticeUpdate update);

    /// <summary>
    /// Adds a partial update provided its entity still has the expected revision at
    /// execution. Success consumes the update. A refusal leaves both builders as they were.
    /// </summary>
    /// <param name="update">An unsubmitted update made for this database instance.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <exception cref="ArgumentNullException">The update is null.</exception>
    /// <exception cref="ArgumentException">The update belongs to another database instance or the batch already addresses this entity.</exception>
    /// <exception cref="InvalidOperationException">The update is empty, or the update or batch is already submitted.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/> when copied operands violate the schema.
    /// <see cref="LatticeErrorEnum.WriteRejected"/> when the instance cannot hold the addition.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The database of the batch is closed.</exception>
    void Update(LatticeUpdate update, LatticeRevision expectedRevision);

    /// <summary>
    /// Submits the batch. Completes when the master has published the whole batch and,
    /// when it was submitted through a child, that child has applied it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A batch is submitted once. After this call it accepts no more operations and cannot
    /// be submitted again, whatever the outcome; build a new batch instead.
    /// </para>
    /// <para>
    /// A batch refused for its operations names one of them, in two phases: first each operation
    /// is checked against the state before the batch, in the order they were added, and the first
    /// that fails a condition of its own is named; when none does, the operation with the lowest
    /// number that would give a value of a unique index a second owner on the state the batch
    /// would leave, with the index declared first when it does so in several. A refusal for memory
    /// names no operation.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the batch while it has not started executing.</param>
    /// <returns>
    /// The position that includes the whole batch and whether any operation changed the
    /// database. An update contributes a change if any of its commands changed the entity.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The batch is empty or has already been submitted.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// An uncancelled submission of a valid batch to a closed database. Reported through the
    /// returned task.
    /// </exception>
    /// <exception cref="LatticeException">
    /// The error of the operation that made the master refuse the batch:
    /// <see cref="LatticeErrorEnum.DuplicateKey"/>, <see cref="LatticeErrorEnum.NotFound"/>,
    /// <see cref="LatticeErrorEnum.RevisionConflict"/>, <see cref="LatticeErrorEnum.UpdateTargetMissing"/>,
    /// <see cref="LatticeErrorEnum.ValidationFailed"/> or
    /// <see cref="LatticeErrorEnum.UniqueConstraintViolation"/>, with
    /// <see cref="LatticeException.OperationIndex"/> set. For the batch as a whole:
    /// <see cref="LatticeErrorEnum.WriteRejected"/>,
    /// <see cref="LatticeErrorEnum.IndeterminateOutcome"/>,
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/> or
    /// <see cref="LatticeErrorEnum.Unavailable"/>.
    /// </exception>
    ValueTask<LatticeWriteResult> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Submits the batch to be executed after the position the session depends on, and
    /// moves the session forward to the position of the batch. Otherwise as
    /// <see cref="CommitAsync(CancellationToken)"/>.
    /// </summary>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the batch while it has not started executing.</param>
    /// <exception cref="InvalidOperationException">
    /// The batch is empty or has already been submitted.
    /// </exception>
    /// <exception cref="LatticeException">
    /// As for <see cref="CommitAsync(CancellationToken)"/>, and
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/> when the session belongs to another
    /// database history.
    /// </exception>
    ValueTask<LatticeWriteResult> CommitAsync(LatticeSession session, CancellationToken cancellationToken = default);
}
