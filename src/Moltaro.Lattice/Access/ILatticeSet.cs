using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// The entities of one entity type as records, without a .NET class: read, enumerate,
/// insert, replace, upsert and delete. Obtained from <see cref="ILatticeDatabase.Set(string)"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every entity type of the schema is available this way, whether or not a .NET class is
/// declared for it. The data and the rules are the same as for
/// <see cref="ILatticeSet{TEntity}"/>, member for member: records are owned copies; the
/// synchronous reads return what this node has now, the asynchronous ones first wait for
/// the position of a <see cref="LatticeSession"/>; writes are executed by the master and,
/// through a child, complete when that child has applied them or has been closed; do not
/// change a record passed to a write, its collections or its byte arrays until the write
/// completes, because the database reads them while the call runs; the same failures are
/// reported.
/// A record of another entity type is an <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// A primary key argument has the key type declared in the schema, or for an integer key an
/// integer of another width whose value fits it: 5 given as an <see cref="int"/> and as a
/// <see cref="long"/> address one entity. The key is passed in the type it is given in, as through
/// a class, so that the call does not box it; a key held as an <see cref="object"/>, such as a
/// value read from a record, is given as one, and the value it holds is the key. Nothing is
/// parsed: a key of any other type, an integer that does not fit and a String key that is not
/// well-formed text, with a surrogate without its pair, are an <see cref="ArgumentException"/>.
/// </para>
/// <para>
/// Writes to one entity are executed in the order their calls were made: a call that is made
/// after an earlier call has returned its task is executed after it, whether or not the earlier
/// task was awaited. A write that first waits for the position of its session takes its place in
/// that order when the wait ends, so a later call may be executed before it. Calls that race on
/// different threads have no order between them. Writes to different entities have no order
/// beyond their positions.
/// </para>
/// </remarks>
public interface ILatticeSet
{
    /// <summary>The entity type this set serves.</summary>
    LatticeEntityType EntityType { get; }

    /// <summary>Starts a local partial update for the given key without reading the entity.</summary>
    /// <typeparam name="TKey">The type of the key supplied by the caller.</typeparam>
    /// <param name="key">The primary key, bound by the same rules as a read of this set.</param>
    /// <returns>An empty update of this entity type and database instance.</returns>
    /// <exception cref="ArgumentException">The key has an incompatible type or value.</exception>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    /// <exception cref="LatticeException"><see cref="LatticeErrorEnum.Unavailable"/> when the database is not ready.</exception>
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
    ILatticeIndex<LatticeRecord> Index(string indexName);

    /// <summary>Reads the entity with the given primary key from what this node has now.</summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <returns>
    /// An owned copy of the entity; <see langword="null"/> when no entity has this key.
    /// </returns>
    LatticeRecord? Get<TKey>(TKey key)
        where TKey : notnull;

    /// <summary>Reads the entity with the given primary key from what this node has now.</summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="record">An owned copy of the entity, when found.</param>
    /// <returns><see langword="true"/> when the entity exists.</returns>
    bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out LatticeRecord record)
        where TKey : notnull;

    /// <summary>
    /// Reads the entity with the given primary key, together with its revision, from what
    /// this node has now.
    /// </summary>
    /// <typeparam name="TKey">The type of the key value.</typeparam>
    /// <param name="key">The primary key.</param>
    /// <param name="record">An owned copy of the entity, when found.</param>
    /// <param name="revision">The revision of exactly the returned state, when found.</param>
    /// <returns><see langword="true"/> when the entity exists.</returns>
    bool TryGet<TKey>(TKey key, [MaybeNullWhen(false)] out LatticeRecord record, out LatticeRevision revision)
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
    /// The record, when the entity exists, its revision and the position of the answer.
    /// </returns>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/>: this node did not reach the
    /// required position in time; nothing was read.
    /// </exception>
    ValueTask<LatticeReadResult<LatticeRecord>> GetAsync<TKey>(TKey key, LatticeSession session, CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>
    /// Runs a short synchronous viewer over one state of the entity in place, when the entity exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The viewer and everything it borrows are valid only during the call. The viewer must
    /// return promptly, without performing I/O, waiting or calling the database. The entity
    /// can be replaced during the call, while the viewer keeps reading the state it started with.
    /// </para>
    /// <para>
    /// An open viewer holds back reclamation of all memory replaced since it began. Exhausting
    /// the memory budget can make writes and a child's application of them wait. Closing also
    /// waits for active viewers, and new reads wait if all protection slots are occupied.
    /// </para>
    /// </remarks>
    /// <typeparam name="TKey">The type of primary key supplied.</typeparam>
    /// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
    /// <param name="key">The primary key, bound by the same rules as an owned read.</param>
    /// <param name="viewer">Called once with the protected view when the entity exists.</param>
    /// <param name="result">The viewer's result on a hit, or its type's default on a miss.</param>
    /// <returns>Whether the entity exists. The viewer is skipped when the key is absent.</returns>
    /// <exception cref="ArgumentException">The key has an incompatible type or value.</exception>
    /// <exception cref="ArgumentNullException">The key or viewer is null.</exception>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    /// <exception cref="LatticeException"><see cref="LatticeErrorEnum.Unavailable"/> when the database is unavailable.</exception>
    bool TryView<TKey, TResult>(TKey key, LatticeViewFunc<LatticeRecordView, TResult> viewer, [MaybeNullWhen(false)] out TResult result)
        where TKey : notnull;

    /// <summary>
    /// Runs a short synchronous viewer with caller-supplied state over the entity in place.
    /// The lifetime and failure rules of <see cref="TryView{TKey, TResult}"/> apply.
    /// </summary>
    /// <typeparam name="TKey">The type of primary key supplied.</typeparam>
    /// <typeparam name="TState">The type of state supplied to the viewer.</typeparam>
    /// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
    /// <param name="key">The primary key, bound by the same rules as an owned read.</param>
    /// <param name="state">State supplied unchanged to the viewer.</param>
    /// <param name="viewer">Called once with the protected view and state when the entity exists.</param>
    /// <param name="result">The viewer's result on a hit, or its type's default on a miss.</param>
    /// <returns>Whether the entity exists. The viewer is skipped when the key is absent.</returns>
    /// <exception cref="ArgumentException">The key has an incompatible type or value.</exception>
    /// <exception cref="ArgumentNullException">The key or viewer is null.</exception>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    /// <exception cref="LatticeException"><see cref="LatticeErrorEnum.Unavailable"/> when the database is unavailable.</exception>
    bool TryView<TKey, TState, TResult>(TKey key, TState state, LatticeViewFunc<LatticeRecordView, TState, TResult> viewer, [MaybeNullWhen(false)] out TResult result)
        where TKey : notnull;

    /// <summary>
    /// Waits for the session's position before taking a view and running the synchronous viewer.
    /// Advances the session to the position of the answer on a hit or a miss.
    /// The view has the lifetime defined by <see cref="TryView{TKey, TResult}"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of primary key supplied.</typeparam>
    /// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
    /// <param name="key">The primary key, bound by the same rules as an owned read.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="viewer">Called once with the protected view when the entity exists.</param>
    /// <param name="cancellationToken">Cancels waiting for the position or entry into the read.</param>
    /// <returns>Whether the entity exists, the viewer's result on a hit, and the position of the answer.</returns>
    /// <exception cref="ArgumentException">The key has an incompatible type or value.</exception>
    /// <exception cref="ArgumentNullException">The key, session or viewer is null.</exception>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/> when the session belongs to another history;
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/> when the session wait times out;
    /// <see cref="LatticeErrorEnum.Unavailable"/> when the database is unavailable.
    /// </exception>
    ValueTask<LatticeViewResult<TResult>> ViewAsync<TKey, TResult>(TKey key, LatticeSession session, LatticeViewFunc<LatticeRecordView, TResult> viewer, CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>
    /// Waits for the session's position before taking a view and running the synchronous viewer
    /// with caller-supplied state. Advances the session to the position of the answer on a hit
    /// or a miss. The view has the lifetime defined by <see cref="TryView{TKey, TResult}"/>.
    /// </summary>
    /// <typeparam name="TKey">The type of primary key supplied.</typeparam>
    /// <typeparam name="TState">The type of state supplied to the viewer.</typeparam>
    /// <typeparam name="TResult">The type of result, independent of the view's lifetime.</typeparam>
    /// <param name="key">The primary key, bound by the same rules as an owned read.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="state">State supplied unchanged to the viewer.</param>
    /// <param name="viewer">Called once with the protected view and state when the entity exists.</param>
    /// <param name="cancellationToken">Cancels waiting for the position or entry into the read.</param>
    /// <returns>Whether the entity exists, the viewer's result on a hit, and the position of the answer.</returns>
    /// <exception cref="ArgumentException">The key has an incompatible type or value.</exception>
    /// <exception cref="ArgumentNullException">The key, session or viewer is null.</exception>
    /// <exception cref="ObjectDisposedException">The database is closed.</exception>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.HistoryMismatch"/> when the session belongs to another history;
    /// <see cref="LatticeErrorEnum.SynchronizationTimeout"/> when the session wait times out;
    /// <see cref="LatticeErrorEnum.Unavailable"/> when the database is unavailable.
    /// </exception>
    ValueTask<LatticeViewResult<TResult>> ViewAsync<TKey, TState, TResult>(TKey key, LatticeSession session, TState state, LatticeViewFunc<LatticeRecordView, TState, TResult> viewer, CancellationToken cancellationToken = default)
        where TKey : notnull;

    /// <summary>
    /// Enumerates owned copies of all entities of the entity type from what this node has
    /// now, in no particular order, within the read limits of the database options. The
    /// traversal and the limits follow the rules of <see cref="ILatticeSet{TEntity}.Enumerate"/>.
    /// </summary>
    /// <remarks>
    /// The engine remembers the keys it has returned for the length of the pass, so that none
    /// is returned twice; that memory grows with the pass and is what a memory limit of a read
    /// bounds.
    /// </remarks>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.ReadLimitExceeded"/>: the step would have exceeded a read
    /// limit of the options, which <see cref="LatticeException.Limit"/> names.
    /// </exception>
    /// <exception cref="ObjectDisposedException">A step came after the database was closed.</exception>
    IEnumerable<LatticeRecord> Enumerate();

    /// <summary>
    /// Enumerates owned copies of all entities of the entity type after this node has
    /// applied the position the session depends on, within the read limits of the database
    /// options. The session moves forward to cover everything the enumeration returns, also
    /// when it returns nothing. The traversal and the limits follow the rules of
    /// <see cref="ILatticeSet{TEntity}.EnumerateAsync"/>.
    /// </summary>
    /// <remarks>
    /// A wait in flight when the instance is closed ends in <see cref="LatticeErrorEnum.Unavailable"/>:
    /// a first step that waits for the position of the session when the instance is closed
    /// ends so. A step after the close of an enumeration that had started is
    /// <see cref="ObjectDisposedException"/>.
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
    IAsyncEnumerable<LatticeRecord> EnumerateAsync(LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>Creates a new entity. The primary key is taken from the record.</summary>
    /// <param name="record">The complete entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.DuplicateKey"/>: an entity with this key already exists.
    /// </exception>
    ValueTask<LatticeWriteResult> InsertAsync(LatticeRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new entity, after the position the session depends on. Moves the session
    /// forward to the position of the write.
    /// </summary>
    /// <param name="record">The complete entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.DuplicateKey"/>: an entity with this key already exists.
    /// </exception>
    ValueTask<LatticeWriteResult> InsertAsync(LatticeRecord record, LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>Replaces an existing entity as a whole.</summary>
    /// <param name="record">The complete new state of the entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.NotFound"/>: no entity with this key exists.
    /// </exception>
    ValueTask<LatticeWriteResult> ReplaceAsync(LatticeRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing entity as a whole, after the position the session depends on.
    /// Moves the session forward to the position of the write.
    /// </summary>
    /// <param name="record">The complete new state of the entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.NotFound"/>: no entity with this key exists.
    /// </exception>
    ValueTask<LatticeWriteResult> ReplaceAsync(LatticeRecord record, LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing entity as a whole, provided it still has the expected revision.
    /// </summary>
    /// <param name="record">The complete new state of the entity.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.RevisionConflict"/>: the entity has another revision or
    /// no longer exists.
    /// </exception>
    ValueTask<LatticeWriteResult> ReplaceAsync(LatticeRecord record, LatticeRevision expectedRevision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces an existing entity as a whole, provided it still has the expected revision,
    /// after the position the session depends on. Moves the session forward to the position
    /// of the write.
    /// </summary>
    /// <param name="record">The complete new state of the entity.</param>
    /// <param name="expectedRevision">The revision read together with the entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.RevisionConflict"/>: the entity has another revision or
    /// no longer exists.
    /// </exception>
    ValueTask<LatticeWriteResult> ReplaceAsync(LatticeRecord record, LatticeRevision expectedRevision, LatticeSession session, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the entity when its key is absent, otherwise replaces the existing entity
    /// as a whole.
    /// </summary>
    /// <param name="record">The complete entity.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    ValueTask<LatticeWriteResult> UpsertAsync(LatticeRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the entity when its key is absent, otherwise replaces the existing entity as
    /// a whole, after the position the session depends on. Moves the session forward to the
    /// position of the write.
    /// </summary>
    /// <param name="record">The complete entity.</param>
    /// <param name="session">The consistency context of the caller.</param>
    /// <param name="cancellationToken">Cancels the write while it has not started executing.</param>
    ValueTask<LatticeWriteResult> UpsertAsync(LatticeRecord record, LatticeSession session, CancellationToken cancellationToken = default);

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
