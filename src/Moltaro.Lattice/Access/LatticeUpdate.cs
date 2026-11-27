using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Access.Updates;
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
    private readonly IUpdateExecutor _executor;
    private readonly IReadOnlyList<BoundProperty>? _fields;
    private readonly UpdateOutcome _outcome = new();
    private List<UpdateCommand>? _commands = [];
    private HashSet<string>? _assigned;

    internal LatticeUpdate(LatticeEntityType entityType, object key, object database, IUpdateExecutor executor, IReadOnlyList<BoundProperty>? fields)
    {
        EntityType = entityType;
        Key = key;
        Database = database;
        _executor = executor;
        _fields = fields;
    }

    /// <summary>The entity type whose fields the commands address.</summary>
    public LatticeEntityType EntityType { get; }

    /// <summary>The number of commands added.</summary>
    public int Count { get; private set; }

    internal object Key { get; }

    internal object Database { get; }

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
    /// <example>
    /// To clear a nullable Int32 field or a nullable List of Int32, use a compatible type
    /// for the null operand, for example <c>update.Assign&lt;int?&gt;("Rank", null)</c> or
    /// <c>update.Assign&lt;List&lt;int&gt;?&gt;("Scores", null)</c>.
    /// </example>
    public LatticeCommandResult Assign<TValue>(string field, TValue value) =>
        Add(UpdateCommandKindEnum.Assign, field, 0, value, (object?)null);

    /// <summary>Appends an element to an existing List, after all its elements at execution.</summary>
    /// <typeparam name="TElement">The type of the element supplied by the caller.</typeparam>
    /// <param name="field">The List field name or path.</param>
    /// <param name="element">The element, copied when the command is added.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult ListAppend<TElement>(string field, TElement element) =>
        Add(UpdateCommandKindEnum.ListAppend, field, 0, element, (object?)null);

    /// <summary>Inserts an element before the given position of an existing List, or at its end.</summary>
    /// <typeparam name="TElement">The type of the element supplied by the caller.</typeparam>
    /// <param name="field">The List field name or path.</param>
    /// <param name="position">The zero-based position, from zero through the List count at execution.</param>
    /// <param name="element">The element, copied when the command is added.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult ListInsert<TElement>(string field, int position, TElement element) =>
        Add(UpdateCommandKindEnum.ListInsert, field, position, element, (object?)null);

    /// <summary>Replaces the element at a position of an existing List, counting equal replacement as a change.</summary>
    /// <typeparam name="TElement">The type of the element supplied by the caller.</typeparam>
    /// <param name="field">The List field name or path.</param>
    /// <param name="position">The zero-based position of an element at execution.</param>
    /// <param name="element">The new element, copied when the command is added.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult ListReplace<TElement>(string field, int position, TElement element) =>
        Add(UpdateCommandKindEnum.ListReplace, field, position, element, (object?)null);

    /// <summary>Removes the element at a position of an existing List.</summary>
    /// <param name="field">The List field name or path.</param>
    /// <param name="position">The zero-based position of an element at execution.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult ListRemove(string field, int position) =>
        Add(UpdateCommandKindEnum.ListRemove, field, position, (object?)null, (object?)null);

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
    public LatticeCommandResult MapSet<TKey, TValue>(string field, TKey key, TValue value) =>
        Add(UpdateCommandKindEnum.MapSet, field, 0, key, value);

    /// <summary>Removes an entry from an existing Map. An absent key leaves the Map unchanged.</summary>
    /// <typeparam name="TKey">The type of the Map key supplied by the caller.</typeparam>
    /// <param name="field">The Map field name or path.</param>
    /// <param name="key">The key, compared as declared in the schema.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult MapRemove<TKey>(string field, TKey key) =>
        Add(UpdateCommandKindEnum.MapRemove, field, 0, key, (object?)null);

    /// <summary>Adds a member to an existing Set unless an equal member already exists.</summary>
    /// <typeparam name="TMember">The type of the member supplied by the caller.</typeparam>
    /// <param name="field">The Set field name or path.</param>
    /// <param name="member">The member, compared as declared in the schema.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult SetAdd<TMember>(string field, TMember member) =>
        Add(UpdateCommandKindEnum.SetAdd, field, 0, member, (object?)null);

    /// <summary>Removes a member from an existing Set. An absent member leaves the Set unchanged.</summary>
    /// <typeparam name="TMember">The type of the member supplied by the caller.</typeparam>
    /// <param name="field">The Set field name or path.</param>
    /// <param name="member">The member, compared as declared in the schema.</param>
    /// <returns>The outcome of this command, available after successful completion.</returns>
    /// <exception cref="ArgumentException">The target or operand is invalid.</exception>
    /// <exception cref="InvalidOperationException">The update is already submitted.</exception>
    public LatticeCommandResult SetRemove<TMember>(string field, TMember member) =>
        Add(UpdateCommandKindEnum.SetRemove, field, 0, member, (object?)null);

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
    /// <exception cref="ObjectDisposedException">
    /// An uncancelled submission of a valid update to a closed database. Reported through the
    /// returned task.
    /// </exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ValidationFailed"/> when the copied operands or resulting
    /// values violate the schema, <see cref="LatticeErrorEnum.NotFound"/> when the entity
    /// does not exist, <see cref="LatticeErrorEnum.UpdateTargetMissing"/> when a command
    /// cannot reach its target, <see cref="LatticeErrorEnum.UniqueConstraintViolation"/>
    /// when the final state violates a unique index, or an error of any write.
    /// </exception>
    public ValueTask<LatticeWriteResult> CommitAsync(CancellationToken cancellationToken = default) =>
        Commit(null, null, cancellationToken);

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
    public ValueTask<LatticeWriteResult> CommitAsync(LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Commit(null, session, cancellationToken);
    }

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
        Commit(expectedRevision, null, cancellationToken);

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
    public ValueTask<LatticeWriteResult> CommitAsync(LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Commit(expectedRevision, session, cancellationToken);
    }

    private LatticeCommandResult Add<TFirst, TSecond>(UpdateCommandKindEnum kind, string field, int position, TFirst first, TSecond second)
    {
        ThrowIfSubmitted();
        ArgumentException.ThrowIfNullOrEmpty(field);
        var (path, binding) = Resolve(field);
        var declaration = path[^1].Declaration;
        var type = declaration.Type;
        var required = kind switch
        {
            UpdateCommandKindEnum.Assign => type,
            UpdateCommandKindEnum.MapSet or UpdateCommandKindEnum.MapRemove => LatticeFieldTypeEnum.Map,
            UpdateCommandKindEnum.SetAdd or UpdateCommandKindEnum.SetRemove => LatticeFieldTypeEnum.Set,
            _ => LatticeFieldTypeEnum.List,
        };
        if (type != required || type == LatticeFieldTypeEnum.Owned || ReferenceEquals(path[0], EntityType.Key))
        {
            throw new ArgumentException($"The command {kind} cannot address '{EntityType.Name}.{field}'.", nameof(field));
        }

        var scalarAssignment = kind == UpdateCommandKindEnum.Assign && type is not (LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set);
        if (scalarAssignment && _assigned?.Contains(field) == true)
        {
            throw new ArgumentException($"The update already assigns '{EntityType.Name}.{field}'.", nameof(field));
        }

        var firstBinding = kind switch
        {
            UpdateCommandKindEnum.Assign => binding,
            UpdateCommandKindEnum.MapSet or UpdateCommandKindEnum.MapRemove => binding?.Key,
            _ => binding?.Element,
        };
        var copiedFirst = UpdateCommand.FirstOf(kind, declaration) is { } firstDeclaration
            ? UpdateValues.Capture(firstDeclaration, first, firstBinding, field)
            : null;
        var copiedSecond = kind == UpdateCommandKindEnum.MapSet
            ? UpdateValues.Capture(declaration.Value!, second, binding?.Value, field)
            : null;

        if (scalarAssignment)
        {
            (_assigned ??= new HashSet<string>(StringComparer.Ordinal)).Add(field);
        }

        _commands!.Add(new UpdateCommand(kind, field, path, position, copiedFirst, copiedSecond));
        return new LatticeCommandResult(_outcome, Count++);
    }

    private (LatticeField[] Path, UpdateValueBinding? Binding) Resolve(string field)
    {
        var steps = field.Split('.');
        var path = new LatticeField[steps.Length];
        if (!EntityType.TryGetField(steps[0], out var first))
        {
            throw InvalidPath(field);
        }

        path[0] = first;
        var binding = _fields?[first.Ordinal].Update;
        for (var index = 1; index < steps.Length; index++)
        {
            if (path[index - 1].Declaration.OwnedType is not { } owner ||
                !owner.TryGetField(steps[index], out var next))
            {
                throw InvalidPath(field);
            }

            path[index] = next;
            binding = binding?.OwnedFields?[next.Ordinal].Update;
        }

        return (path, binding);
    }

    private ArgumentException InvalidPath(string field) =>
        new($"'{EntityType.Name}.{field}' is not a field path through owned values.", nameof(field));

    internal void ThrowIfSubmitted()
    {
        if (_commands is null)
        {
            throw new InvalidOperationException("The update has already been submitted. Build a new update.");
        }
    }

    internal void ThrowIfEmpty()
    {
        if (Count == 0)
        {
            throw new InvalidOperationException("The update is empty; there is nothing to commit.");
        }
    }

    internal void Consume()
    {
        _commands = null;
        _assigned = null;
    }

    internal UpdateInput ValidateContent() => ValidateContent(_commands!);

    private UpdateInput ValidateContent(List<UpdateCommand> commands)
    {
        var validated = new UpdateCommand[commands.Count];
        for (var index = 0; index < validated.Length; index++)
        {
            var command = commands[index];
            var field = command.Path[^1];
            var declaration = field.Declaration;
            var first = UpdateCommand.FirstOf(command.Kind, declaration) is { } firstDeclaration
                ? UpdateValues.Validate(firstDeclaration, command.First, EntityType, field)
                : null;
            var second = command.Kind == UpdateCommandKindEnum.MapSet
                ? UpdateValues.Validate(declaration.Value!, command.Second, EntityType, field)
                : null;
            validated[index] = command with { First = first, Second = second };
        }

        return new UpdateInput(validated, _outcome);
    }

    private ValueTask<LatticeWriteResult> Commit(LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken)
    {
        ThrowIfSubmitted();
        ThrowIfEmpty();
        var commands = _commands!;
        Consume();

        try
        {
            var input = ValidateContent(commands);
            var result = _executor.ExecuteUpdateAsync(Key, input, expectedRevision, session, cancellationToken).AsTask();
            _outcome.Attach(result);
            return new ValueTask<LatticeWriteResult>(result);
        }
        catch (Exception failure)
        {
            return ValueTask.FromException<LatticeWriteResult>(failure);
        }
    }
}
