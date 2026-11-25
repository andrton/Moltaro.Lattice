using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// The entities of one entity type as objects of a .NET class: read, enumerate, insert,
/// replace, upsert and delete. Obtained from <see cref="ILatticeDatabase.Set{TEntity}"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads</b> are served from local memory and return owned copies. A copy never changes
/// when the database changes, and editing a copy changes nothing in the database until it
/// is submitted by a write. Two reads share no object at any depth, but for an empty array, of
/// a Binary value or of a List, which is the one empty array of its type,
/// <see cref="Array.Empty{T}"/>, that no caller can change. Each returned entity is coherent;
/// separate reads do not share a snapshot. Only the fields declared in the schema are stored, each in the property that
/// <see cref="ILatticeDatabase.Set{TEntity}"/> binds to it; other properties of the class
/// are ignored.
/// </para>
/// <para>
/// There are two kinds of reads. The synchronous ones, <see cref="Get"/>,
/// <see cref="TryGet{TKey}(TKey, out TEntity)"/> and <see cref="Enumerate"/>, return what
/// this node has at the moment; on a child that can be older than the master. The
/// asynchronous ones, <see cref="GetAsync"/> and <see cref="EnumerateAsync"/>, take a
/// <see cref="LatticeSession"/>: they first wait until this node has applied the position
/// the session depends on, and fail with
/// <see cref="LatticeErrorEnum.SynchronizationTimeout"/> rather than return older data.
/// </para>
/// <para>
/// <b>Writes</b> are executed by the master, which orders conflicting writes. A write
/// completes successfully only after the master has published the change and, in Durable
/// mode, the storage has acknowledged it. A write submitted through a child additionally
/// waits until that child has applied it, so the child's next read sees it; it does not
/// wait for other children. When the child is closed during that wait, the call returns the
/// result of the write at once, since a closed child serves no read. A failed write changes
/// nothing.
/// </para>
/// <para>
/// A valid write called after the database reaches <see cref="LatticeDatabaseStateEnum.Closed"/>
/// reports <see cref="ObjectDisposedException"/> through the returned task, including through
/// a set obtained earlier.
/// </para>
/// <para>
/// Writes to one entity are executed in the order their calls were made: a call that is made
/// after an earlier call has returned its task is executed after it, whether or not the earlier
/// task was awaited. A write that first waits for the position of its session takes its place in
/// that order when the wait ends, so a later call may be executed before it. Calls that race on
/// different threads have no order between them. Writes to different entities have no order
/// beyond their positions.
/// </para>
/// <para>
/// A write that takes a <see cref="LatticeSession"/> is executed by the master after the
/// position the session depends on, and moves the session forward to its own position.
/// </para>
/// <para>
/// When a write through a child is committed but the child does not apply it within the
/// synchronization timeout, the write fails with
/// <see cref="LatticeErrorEnum.SynchronizationTimeout"/> and
/// <see cref="LatticeException.CommittedPosition"/> set. The write is not rolled back and
/// must not be submitted again.
/// </para>
/// <para>
/// Do not change the entity, its collections or its byte arrays until the write completes:
/// the database reads them while the call runs. The primary key of an entity never changes;
/// writing an entity with another key addresses another entity.
/// </para>
/// <para>
/// A primary key argument has the key type declared in the schema; an integer of another
/// width is accepted when it fits. Any other key type is an <see cref="ArgumentException"/>,
/// and so is a String key that is not well-formed text, with a surrogate without its pair.
/// </para>
/// <para>
/// Cancellation takes effect only while an operation is waiting or has not started
/// executing: an <see cref="OperationCanceledException"/> means that a write was not executed.
/// Through a child, a write has started once the child begins to send it to its master: a
/// cancellation after that does not withdraw it, and the call returns the real outcome.
/// </para>
/// <para>
/// Every member throws <see cref="LatticeException"/> with
/// <see cref="LatticeErrorEnum.Unavailable"/> when the database cannot serve the operation,
/// and every member that takes a session with <see cref="LatticeErrorEnum.HistoryMismatch"/>
/// when the session belongs to another database history. Every write additionally throws
/// it with <see cref="LatticeErrorEnum.ValidationFailed"/>,
/// <see cref="LatticeErrorEnum.WriteRejected"/> or
/// <see cref="LatticeErrorEnum.IndeterminateOutcome"/>, and a write that creates or replaces
/// an entity with <see cref="LatticeErrorEnum.UniqueConstraintViolation"/> when another
/// entity already has its values in a unique index. All members are safe to call from
/// several threads.
/// </para>
/// <para>
/// A write checks its arguments, such as the type of a key, which is an
/// <see cref="ArgumentException"/>, and validates the entity against the schema at every
/// depth: a field without the value it requires, a <see langword="null"/> where none is
/// allowed in a collection or an owned value, two Map keys that the key declaration holds
/// equal, or an array whose elements the runtime would read as other values, at any depth,
/// such as a <c>uint[]</c> in an <c>int[]</c> property or an <c>sbyte[]</c> in a
/// <c>byte[]</c> one, is <see cref="LatticeErrorEnum.ValidationFailed"/>. Text that is not
/// well-formed — a surrogate without its pair — is refused in the same way, at any depth, and
/// is never stored and never replaced by another character. A String or Binary value that is
/// larger than one value may be, as <see cref="LatticeFieldTypeEnum.String"/> and
/// <see cref="LatticeFieldTypeEnum.Binary"/> state, is refused at any depth. This applies
/// to collection keys, values and members as well as to fields. A collection above its
/// entry limit is refused in the same way, as <see cref="LatticeFieldTypeEnum.List"/>,
/// <see cref="LatticeFieldTypeEnum.Map"/> and <see cref="LatticeFieldTypeEnum.Set"/> state.
/// When members of a Set are equal under its declaration, the first in the order the input
/// collection gives them is kept, whatever comparer the input had. An integer
/// that the enum type of its field does not declare — for a Flags type, a bit that no member
/// declares — is refused in the same way. Which arrays
/// the runtime would read as other values, and that an array of a class derived from the
/// declared element type is valid, is stated at <see cref="LatticeRecord.this[string]"/>,
/// which refuses such arrays in the same way. Validation precedes the conditions of the
/// write, including whether the key exists and whether the entity has the expected revision.
/// Whether a unique index already holds a
/// value of the entity is decided after the conditions of the write, by the master, as
/// <see cref="LatticeIndex.IsUnique"/> states.
/// </para>
/// <para>
/// Each write here is atomic for its one entity. Writes that have to succeed or fail
/// together are submitted as one <see cref="ILatticeWriteBatch"/>.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The .NET class declared for the entity type in the schema.</typeparam>
public interface ILatticeSet<TEntity>
    where TEntity : class
{
    /// <summary>The entity type this set serves.</summary>
    LatticeEntityType EntityType { get; }

    /// <inheritdoc cref="ILatticeSet.CreateUpdate{TKey}"/>
    LatticeUpdate CreateUpdate<TKey>(TKey key)
        where TKey : notnull;

    /// <summary>
    /// Selects a secondary index declared for the entity type, to find entities by the
    /// values of its fields. The returned object can be kept and reused.
    /// </summary>
    /// <param name="indexName">The index name declared in the schema.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/>: the entity type declares no such index.
    /// </exception>
    ILatticeIndex<TEntity> Index(string indexName);

    /// <summary>Reads the entity with the given primary key from what this node has now.</summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <returns>
    /// An owned copy of the entity; <see langword="null"/> when no entity has this key.
    /// </returns>
    TEntity? Get<TKey>(TKey key)
        where TKey : notnull;

    /// <summary>Reads the entity with the given primary key from what this node has now.</summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="entity">An owned copy of the entity, when found.</param>
    /// <returns><see langword="true"/> when the entity exists.</returns>
    bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out TEntity entity)
        where TKey : notnull;

    /// <summary>
    /// Reads the entity with the given primary key, together with its revision, from what
    /// this node has now.
    /// </summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="entity">An owned copy of the entity, when found.</param>
    /// <param name="revision">The revision of exactly the returned state, when found.</param>
    /// <returns><see langword="true"/> when the entity exists.</returns>
    bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out TEntity entity, out LatticeRevision revision)
        where TKey : notnull;

    /// <summary>
    /// Reads the entity with the given primary key after this node has applied the position
    /// the session depends on. Moves the session forward to the position of the answer.
    /// </summary>
    /// <remarks>
    /// A wait in flight when the instance is closed ends in <see cref="LatticeErrorEnum.Unavailable"/>.
    /// </remarks>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    /// The entity, when it exists, its revision and the position of the answer. "Not found"
    /// is an answer with a position as well.
    /// </returns>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/>: this node did not reach the
    /// required position in time; nothing was read.
    /// </exception>
    ValueTask<LatticeReadResult<TEntity>> GetAsync<TKey>(TKey key, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <inheritdoc cref="ILatticeSet.TryView{TKey, TResult}"/>
    /// <typeparam name="TKey">The type of primary key supplied.</typeparam>
    /// <typeparam name="TView">The generated view of the set's entity class.</typeparam>
    /// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/> if a field of the generated view disagrees
    /// with the set's schema. The viewer is skipped.
    /// </exception>
    bool TryView<TKey, TView, TResult>(TKey key, LatticeViewFunc<TView, TResult> viewer, [MaybeNullWhen(false)] out TResult result)
        where TKey : notnull
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct;

    /// <inheritdoc cref="ILatticeSet.TryView{TKey, TState, TResult}"/>
    /// <typeparam name="TKey">The type of primary key supplied.</typeparam>
    /// <typeparam name="TView">The generated view of the set's entity class.</typeparam>
    /// <typeparam name="TState">The type of state supplied to the viewer.</typeparam>
    /// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/> if a field of the generated view disagrees
    /// with the set's schema. The viewer is skipped.
    /// </exception>
    bool TryView<TKey, TView, TState, TResult>(TKey key, TState state, LatticeViewFunc<TView, TState, TResult> viewer, [MaybeNullWhen(false)] out TResult result)
        where TKey : notnull
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct;

    /// <inheritdoc cref="ILatticeSet.ViewAsync{TKey, TResult}"/>
    /// <typeparam name="TKey">The type of primary key supplied.</typeparam>
    /// <typeparam name="TView">The generated view of the set's entity class.</typeparam>
    /// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/> if a field of the generated view disagrees
    /// with the set's schema. The task fails before the viewer runs.
    /// </exception>
    ValueTask<LatticeViewResult<TResult>> ViewAsync<TKey, TView, TResult>(TKey key, LatticeSession session,
        LatticeViewFunc<TView, TResult> viewer, CancellationToken cancellationToken = default)
        where TKey : notnull
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct;

    /// <inheritdoc cref="ILatticeSet.ViewAsync{TKey, TState, TResult}"/>
    /// <typeparam name="TKey">The type of primary key supplied.</typeparam>
    /// <typeparam name="TView">The generated view of the set's entity class.</typeparam>
    /// <typeparam name="TState">The type of state supplied to the viewer.</typeparam>
    /// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.BindingFailed"/> if a field of the generated view disagrees
    /// with the set's schema. The task fails before the viewer runs.
    /// </exception>
    ValueTask<LatticeViewResult<TResult>> ViewAsync<TKey, TView, TState, TResult>(TKey key, LatticeSession session, TState state,
        LatticeViewFunc<TView, TState, TResult> viewer, CancellationToken cancellationToken = default)
        where TKey : notnull
        where TView : ILatticeEntityView<TView, TEntity>, allows ref struct;

    /// <summary>
    /// Enumerates owned copies of all entities of the entity type from what this node has
    /// now, in no particular order, within the read limits of the database options. The
    /// order may differ between two instances that hold the same data.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The traversal is lazy and runs over live data without blocking writes; a change during
    /// the pass does not end it. The enumeration is not a snapshot. An entity that exists from
    /// the start of the pass to its end is returned exactly once, in one state that was written:
    /// the one before or the one after a replace made during the pass. An entity inserted or
    /// deleted during the pass may or may not be returned. No key is returned twice. Each new
    /// enumeration is a separate pass. The engine remembers the keys it has returned for the
    /// length of the pass, so that none is returned twice; that memory grows with the pass and
    /// is what a memory limit of a read bounds.
    /// </para>
    /// <para>
    /// Nothing is held between two steps, so an enumeration that is abandoned, disposed or
    /// not, leaves nothing behind and holds back no write. A step after the database was
    /// closed throws <see cref="ObjectDisposedException"/>; an enumeration never goes on over
    /// a closed database.
    /// </para>
    /// <para>
    /// <see cref="LatticeDatabaseOptions.ReadMemoryLimit"/> and
    /// <see cref="LatticeDatabaseOptions.ReadTimeLimit"/> apply when they are set; what each
    /// counts is stated there. When neither is set, an enumeration of any length runs to its
    /// end. The step that would exceed a limit throws, and the enumeration fails with it. The
    /// entities returned before stay valid. The enumeration never ends as if it were
    /// complete: every further step fails with the same error.
    /// </para>
    /// </remarks>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ReadLimitExceeded"/>: the step would have exceeded a read
    /// limit of the options, which <see cref="LatticeException.Limit"/> names.
    /// </exception>
    /// <exception cref="ObjectDisposedException">A step came after the database was closed.</exception>
    IEnumerable<TEntity> Enumerate();

    /// <summary>
    /// Enumerates owned copies of all entities of the entity type after this node has
    /// applied the position the session depends on, within the read limits of the database
    /// options. The traversal and the limits follow the rules of <see cref="Enumerate"/>; it
    /// is not a snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session moves forward to cover everything the enumeration returns, also when it
    /// returns nothing. The wait for the position of the session is not counted against
    /// <see cref="LatticeDatabaseOptions.ReadTimeLimit"/>; the timeout of the session bounds
    /// it.
    /// </para>
    /// <para>
    /// A wait in flight when the instance is closed ends in <see cref="LatticeErrorEnum.Unavailable"/>:
    /// a first step that waits for the position of the session when the instance is closed
    /// ends so. A step after the close of an enumeration that had started is
    /// <see cref="ObjectDisposedException"/>.
    /// </para>
    /// </remarks>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait and the enumeration, with <see cref="OperationCanceledException"/>,
    /// never with <see cref="LatticeErrorEnum.ReadLimitExceeded"/>.
    /// </param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/>: this node did not reach the
    /// required position in time; nothing was returned.
    /// <see cref="LatticeErrorEnum.ReadLimitExceeded"/>: the step would have exceeded a read
    /// limit of the options, which <see cref="LatticeException.Limit"/> names.
    /// </exception>
    /// <exception cref="ObjectDisposedException">A step came after the database was closed.</exception>
    IAsyncEnumerable<TEntity> EnumerateAsync(LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>Creates a new entity. The primary key is taken from the entity.</summary>
    /// <param name="entity">The complete entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.DuplicateKey"/>: an entity with this key already exists.
    /// </exception>
    ValueTask<LatticeWriteResult> InsertAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new entity, after the position the session depends on. Moves the session
    /// forward to the position of the write.
    /// </summary>
    /// <param name="entity">The complete entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.DuplicateKey"/>: an entity with this key already exists.
    /// </exception>
    ValueTask<LatticeWriteResult> InsertAsync(TEntity entity, LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing entity as a whole. Nothing of the previous state is merged into
    /// the new one.
    /// </summary>
    /// <param name="entity">The complete new state of the entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.NotFound"/>: no entity with this key exists.
    /// </exception>
    ValueTask<LatticeWriteResult> ReplaceAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing entity as a whole, after the position the session depends on.
    /// Moves the session forward to the position of the write.
    /// </summary>
    /// <param name="entity">The complete new state of the entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.NotFound"/>: no entity with this key exists.
    /// </exception>
    ValueTask<LatticeWriteResult> ReplaceAsync(TEntity entity, LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing entity as a whole, provided it still has the expected revision.
    /// </summary>
    /// <param name="entity">The complete new state of the entity.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.RevisionConflict"/>: the entity has another revision or
    /// no longer exists.
    /// </exception>
    ValueTask<LatticeWriteResult> ReplaceAsync(TEntity entity, LatticeRevision expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing entity as a whole, provided it still has the expected revision,
    /// after the position the session depends on. Moves the session forward to the position
    /// of the write.
    /// </summary>
    /// <param name="entity">The complete new state of the entity.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.RevisionConflict"/>: the entity has another revision or
    /// no longer exists.
    /// </exception>
    ValueTask<LatticeWriteResult> ReplaceAsync(TEntity entity, LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the entity when its key is absent, otherwise replaces the existing entity
    /// as a whole.
    /// </summary>
    /// <param name="entity">The complete entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    ValueTask<LatticeWriteResult> UpsertAsync(TEntity entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the entity when its key is absent, otherwise replaces the existing entity as
    /// a whole, after the position the session depends on. Moves the session forward to the
    /// position of the write.
    /// </summary>
    /// <param name="entity">The complete entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    ValueTask<LatticeWriteResult> UpsertAsync(TEntity entity, LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the entity with the given primary key. An absent key is an ordinary result,
    /// not a failure.
    /// </summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <returns>
    /// A result whose <see cref="LatticeWriteResult.Changed"/> is <see langword="true"/>
    /// when an entity was deleted and <see langword="false"/> when the key was already absent.
    /// </returns>
    ValueTask<LatticeWriteResult> DeleteAsync<TKey>(TKey key, CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>
    /// Deletes the entity with the given primary key, after the position the session
    /// depends on. An absent key is an ordinary result. Moves the session forward to the
    /// position of the answer.
    /// </summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <returns>
    /// A result whose <see cref="LatticeWriteResult.Changed"/> is <see langword="true"/>
    /// when an entity was deleted and <see langword="false"/> when the key was already absent.
    /// </returns>
    ValueTask<LatticeWriteResult> DeleteAsync<TKey>(TKey key, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>
    /// Deletes the entity with the given primary key, provided it still has the expected revision.
    /// </summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.RevisionConflict"/>: the entity has another revision or
    /// no longer exists.
    /// </exception>
    ValueTask<LatticeWriteResult> DeleteAsync<TKey>(TKey key, LatticeRevision expectedRevision, CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>
    /// Deletes the entity with the given primary key, provided it still has the expected
    /// revision, after the position the session depends on. Moves the session forward to
    /// the position of the write.
    /// </summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.RevisionConflict"/>: the entity has another revision or
    /// no longer exists.
    /// </exception>
    ValueTask<LatticeWriteResult> DeleteAsync<TKey>(TKey key, LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull;
}
