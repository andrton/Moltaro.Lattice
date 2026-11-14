namespace Moltaro.Lattice.Access;

/// <summary>
/// Several writes that the database executes together or not at all. Obtained from
/// <see cref="ILatticeDatabase.CreateWriteBatch"/>, filled with operations and submitted
/// once with <see cref="CommitAsync(CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a batch guarantees.</b> The master executes all operations of a batch as one
/// change at one position, or none of them. No reader, on the master or on a child, ever
/// sees a part of a batch: a child applies it as a whole. The operations may address
/// different entity types of the same database.
/// </para>
/// <para>
/// <b>What a batch is not.</b> The batch is one transaction of the database, but not a
/// transaction the application works inside: it cannot read, it holds no lock while the
/// application builds it, and it does not span two databases or an external one. A condition on the current state is expressed by the operation itself: Insert
/// requires an absent key, Replace an existing one, and an expected revision requires
/// that the entity has not changed since it was read.
/// </para>
/// <para>
/// <b>Building.</b> Adding an operation contacts nothing. The entity or record is copied
/// when it is added, so later edits of the object are not part of the batch. A batch
/// takes one operation per entity: adding a second operation for the same entity type and
/// primary key is an <see cref="ArgumentException"/>, and the batch stays as it was. An
/// entity that violates the schema is refused when it is added, with
/// <see cref="LatticeErrorEnum.ValidationFailed"/>. A batch is built by one caller and is
/// not safe to use from several threads at once.
/// </para>
/// <para>
/// <b>Executing.</b> The master checks every operation against the state at the moment of
/// execution, and the unique indexes against the state the batch would leave. Two
/// entities may therefore exchange their values of a unique index inside one batch. When
/// any check fails, the whole batch is refused and nothing is written; the exception
/// carries the error of the failed operation and, in
/// <see cref="LatticeException.OperationIndex"/>, its number. A batch is bounded: the
/// master refuses one that exceeds its limits on the number of operations or on their
/// total size, with <see cref="LatticeErrorEnum.WriteRejected"/>.
/// </para>
/// <para>
/// A batch submitted through a child is executed by the master and completes when that
/// child has applied it. When the child does not apply it within the synchronization
/// timeout, the commit fails with <see cref="LatticeErrorEnum.SynchronizationTimeout"/>
/// and <see cref="LatticeException.CommittedPosition"/> set: the batch is committed, is
/// not rolled back and must not be submitted again.
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
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema declares no entity type for the class.
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>: the entity violates the schema.
    /// </exception>
    void Insert<TEntity>(TEntity entity)
        where TEntity : class;

    /// <summary>Adds the creation of a new entity from a record.</summary>
    /// <param name="record">The complete record, of an entity type of this database.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the record belongs to another schema.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>: the record violates the schema.
    /// </exception>
    void Insert(LatticeRecord record);

    /// <summary>Adds the replacement of an existing entity as a whole.</summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <param name="entity">The complete new state of the entity.</param>
    /// <exception cref="ArgumentException">The batch already has an operation for this entity.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/> or
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>, as for <see cref="Insert{TEntity}"/>.
    /// </exception>
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
    /// </exception>
    void Replace<TEntity>(TEntity entity, LatticeRevision expectedRevision)
        where TEntity : class;

    /// <summary>Adds the replacement of an existing entity as a whole, from a record.</summary>
    /// <param name="record">The complete new state, of an entity type of this database.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the record belongs to another schema.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>: the record violates the schema.
    /// </exception>
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
    /// </exception>
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
    /// </exception>
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
    /// </exception>
    void Upsert(LatticeRecord record);

    /// <summary>
    /// Adds the deletion of the entity with the given primary key. A key that is absent
    /// when the batch is executed is not a failure; that operation changes nothing.
    /// </summary>
    /// <typeparam name="TEntity">A .NET class named by a <c>ClrType</c> in the schema.</typeparam>
    /// <param name="key">The primary key, of the key type declared in the schema.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the key has another type.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema declares no entity type for the class.
    /// </exception>
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
    /// The batch already has an operation for this entity, or the key has another type.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema declares no entity type for the class.
    /// </exception>
    void Delete<TEntity>(object key, LatticeRevision expectedRevision)
        where TEntity : class;

    /// <summary>
    /// Adds the deletion of the entity of the named entity type with the given primary
    /// key. A key that is absent when the batch is executed is not a failure.
    /// </summary>
    /// <param name="entityName">The entity type name declared in the schema.</param>
    /// <param name="key">The primary key, of the key type declared in the schema.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the key has another type.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema has no such entity type.
    /// </exception>
    void Delete(string entityName, object key);

    /// <summary>
    /// Adds the deletion of the entity of the named entity type with the given primary
    /// key, provided it still has the expected revision when the batch is executed.
    /// </summary>
    /// <param name="entityName">The entity type name declared in the schema.</param>
    /// <param name="key">The primary key, of the key type declared in the schema.</param>
    /// <param name="expectedRevision">The revision read together with the record.</param>
    /// <exception cref="ArgumentException">
    /// The batch already has an operation for this entity, or the key has another type.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the schema has no such entity type.
    /// </exception>
    void Delete(string entityName, object key, LatticeRevision expectedRevision);

    /// <summary>
    /// Submits the batch. Completes when the master has published the whole batch and,
    /// when it was submitted through a child, that child has applied it.
    /// </summary>
    /// <remarks>
    /// A batch is submitted once. After this call it accepts no more operations and cannot
    /// be submitted again, whatever the outcome; build a new batch instead.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the batch while it has not started executing.</param>
    /// <returns>
    /// The position that includes the whole batch.
    /// <see cref="LatticeWriteResult.Changed"/> is <see langword="false"/> only when no
    /// operation had anything to do.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The batch is empty or has already been submitted.
    /// </exception>
    /// <exception cref="NotSupportedException">Executing a batch is not implemented yet.</exception>
    /// <exception cref="LatticeException">
    /// The error of the operation that made the master refuse the batch:
    /// <see cref="LatticeErrorEnum.DuplicateKey"/>, <see cref="LatticeErrorEnum.NotFound"/>,
    /// <see cref="LatticeErrorEnum.RevisionConflict"/> or
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
    /// <exception cref="NotSupportedException">Executing a batch is not implemented yet.</exception>
    /// <exception cref="LatticeException">
    /// As for <see cref="CommitAsync(CancellationToken)"/>, and
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/> when the session belongs to another
    /// database history.
    /// </exception>
    ValueTask<LatticeWriteResult> CommitAsync(LatticeSession session, CancellationToken cancellationToken = default);
}
