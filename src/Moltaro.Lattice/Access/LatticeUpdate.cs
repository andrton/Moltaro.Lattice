using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// An ordered list of commands for one existing entity, obtained from
/// <see cref="ILatticeSet.CreateUpdate{TKey}"/>. The application builds it locally
/// without reading the entity or holding a database lock.
/// </summary>
/// <remarks>
/// <para>
/// A field is named directly or by a dot-separated path through owned values, such as
/// <c>Home.City</c>. A path cannot pass through a collection or a reference. The key
/// and a whole owned value cannot be assigned. Each scalar target may be assigned once.
/// Whole-collection assignments and element commands may repeat and run in order.
/// </para>
/// <para>
/// Each command copies its operands when added. An incompatible target, type, null
/// or numeric conversion according to the schema gives <see cref="ArgumentException"/>
/// and leaves this update unchanged. A lossless numeric conversion uses the rules
/// of an index search key. Inside a whole collection, operands use their declared types.
/// Invalid copied content, including malformed text, undeclared enum values and missing
/// required fields inside owned values, is checked upon submission. List positions and
/// the size that results from an edit are checked when the command executes.
/// </para>
/// <para>
/// The commands and their index changes are published together, leaving unmentioned
/// fields as they were at execution. Submit this update once, either with
/// <see cref="CommitAsync(CancellationToken)"/> or
/// <see cref="ILatticeWriteBatch.Update(LatticeUpdate)"/>. A submitted update accepts
/// no more commands. One caller builds the update.
/// </para>
/// </remarks>
public sealed class LatticeUpdate
{
    internal const string NotImplemented = "Partial updates are not implemented yet.";

    private LatticeUpdate()
    {
    }

    /// <summary>The entity type whose fields the commands address.</summary>
    public LatticeEntityType EntityType => throw new NotSupportedException(NotImplemented);

    /// <summary>The number of commands added.</summary>
    public int Count => throw new NotSupportedException(NotImplemented);

    /// <summary>
    /// Assigns a scalar, Binary, Enum or Reference value, or replaces a whole collection.
    /// An assignment counts as changed even when its value is equal to the existing value.
    /// </summary>
    /// <typeparam name="TValue">The type of the value supplied by the caller.</typeparam>
    /// <param name="field">The field name or path.</param>
    /// <param name="value">The new value, copied when the command is added.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid, or a scalar assignment already addresses the target.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult Assign<TValue>(string field, TValue value) => throw new NotSupportedException(NotImplemented);

    /// <summary>Appends an element to an existing List, after all its elements at execution.</summary>
    /// <typeparam name="TElement">The type of the element supplied by the caller.</typeparam>
    /// <param name="field">The List field name or path.</param>
    /// <param name="element">The element, copied when the command is added.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult ListAppend<TElement>(string field, TElement element) => throw new NotSupportedException(NotImplemented);

    /// <summary>Inserts an element before the given position of an existing List, or at its end.</summary>
    /// <typeparam name="TElement">The type of the element supplied by the caller.</typeparam>
    /// <param name="field">The List field name or path.</param>
    /// <param name="position">The zero-based position, from zero through the List count at execution.</param>
    /// <param name="element">The element, copied when the command is added.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult ListInsert<TElement>(string field, int position, TElement element) => throw new NotSupportedException(NotImplemented);

    /// <summary>Replaces the element at a position of an existing List, counting equal replacement as a change.</summary>
    /// <typeparam name="TElement">The type of the element supplied by the caller.</typeparam>
    /// <param name="field">The List field name or path.</param>
    /// <param name="position">The zero-based position of an element at execution.</param>
    /// <param name="element">The new element, copied when the command is added.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult ListReplace<TElement>(string field, int position, TElement element) => throw new NotSupportedException(NotImplemented);

    /// <summary>Removes the element at a position of an existing List.</summary>
    /// <param name="field">The List field name or path.</param>
    /// <param name="position">The zero-based position of an element at execution.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult ListRemove(string field, int position) => throw new NotSupportedException(NotImplemented);

    /// <summary>
    /// Inserts an entry or replaces its value in an existing Map. The key of an equal
    /// existing entry keeps its original spelling. Setting always counts as a change.
    /// </summary>
    /// <typeparam name="TKey">The type of the Map key supplied by the caller.</typeparam>
    /// <typeparam name="TValue">The type of the Map value supplied by the caller.</typeparam>
    /// <param name="field">The Map field name or path.</param>
    /// <param name="key">The key, compared as declared in the schema.</param>
    /// <param name="value">The value, copied when the command is added.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or an operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult MapSet<TKey, TValue>(string field, TKey key, TValue value) => throw new NotSupportedException(NotImplemented);

    /// <summary>Removes an entry from an existing Map. An absent key leaves the Map unchanged.</summary>
    /// <typeparam name="TKey">The type of the Map key supplied by the caller.</typeparam>
    /// <param name="field">The Map field name or path.</param>
    /// <param name="key">The key, compared as declared in the schema.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult MapRemove<TKey>(string field, TKey key) => throw new NotSupportedException(NotImplemented);

    /// <summary>Adds a member to an existing Set unless an equal member already exists.</summary>
    /// <typeparam name="TMember">The type of the member supplied by the caller.</typeparam>
    /// <param name="field">The Set field name or path.</param>
    /// <param name="member">The member, compared as declared in the schema.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult SetAdd<TMember>(string field, TMember member) => throw new NotSupportedException(NotImplemented);

    /// <summary>Removes a member from an existing Set. An absent member leaves the Set unchanged.</summary>
    /// <typeparam name="TMember">The type of the member supplied by the caller.</typeparam>
    /// <param name="field">The Set field name or path.</param>
    /// <param name="member">The member, compared as declared in the schema.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult SetRemove<TMember>(string field, TMember member) => throw new NotSupportedException(NotImplemented);

    /// <summary>Submits these commands as one write in the order they were added.</summary>
    /// <remarks>
    /// A submission that passes the synchronous argument and builder checks consumes the
    /// update. Invalid copied content fails the returned task with
    /// <see cref="LatticeErrorEnum.ValidationFailed"/> before checking cancellation, the
    /// database state or write conditions. A child normally returns the result after applying
    /// the position. If it closes after receiving the master's reply, the call still returns
    /// the master's result and the command outcomes become available.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the write before it starts executing.</param>
    /// <returns>
    /// A result whose position includes the update and whose Changed is true if any
    /// command changed the entity. An unchanged update keeps the revision and has its
    /// own position.
    /// </returns>
    /// <exception cref="InvalidOperationException">The update is empty or already submitted.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/> when the copied operands or resulting
    /// values violate the schema, <see cref="LatticeErrorEnum.NotFound"/> when the entity
    /// does not exist, <see cref="LatticeErrorEnum.UpdateTargetMissing"/> when a command
    /// cannot reach its target, <see cref="LatticeErrorEnum.UniqueConstraintViolation"/>
    /// when the final state violates a unique index, or an error of any write.
    /// </exception>
    public ValueTask<LatticeWriteResult> CommitAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplemented);

    /// <summary>
    /// Submits the update after the position the session depends on and advances the
    /// session to the returned position. Otherwise as <see cref="CommitAsync(CancellationToken)"/>.
    /// </summary>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write before it starts executing.</param>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="LatticeException">
    /// As for <see cref="CommitAsync(CancellationToken)"/>, or
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/> for a session from another history.
    /// </exception>
    public ValueTask<LatticeWriteResult> CommitAsync(LatticeSession session, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplemented);

    /// <summary>
    /// Submits the update provided the entity still has the expected revision at execution.
    /// Otherwise as <see cref="CommitAsync(CancellationToken)"/>.
    /// </summary>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <param name="cancellationToken">Cancels the write before it starts executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.RevisionConflict"/> for a missing entity or a different
    /// revision. Other errors are as for <see cref="CommitAsync(CancellationToken)"/>.
    /// </exception>
    public ValueTask<LatticeWriteResult> CommitAsync(LatticeRevision expectedRevision, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplemented);

    /// <summary>
    /// Submits a revision-checked update after the position the session depends on and
    /// advances the session to the returned position. Otherwise as
    /// <see cref="CommitAsync(LatticeRevision, CancellationToken)"/>.
    /// </summary>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write before it starts executing.</param>
    /// <exception cref="ArgumentNullException">The session is null.</exception>
    /// <exception cref="LatticeException">
    /// As for <see cref="CommitAsync(LatticeRevision, CancellationToken)"/>, or
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/> for a session from another history.
    /// </exception>
    public ValueTask<LatticeWriteResult> CommitAsync(LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(NotImplemented);
}
