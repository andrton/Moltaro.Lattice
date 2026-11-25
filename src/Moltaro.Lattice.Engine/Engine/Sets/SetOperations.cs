using System.Diagnostics.CodeAnalysis;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Binding;
using Moltaro.Lattice.Access.Updates;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Engine.Child.Forwarding;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Sets;

// The set operations for one entity type. Owned reads, enumeration, searches and writes are shared
// by the set of a class and the set by name. A protected read also uses the position clock
// and table to run its callback over a resident root. A write goes to the write coordinator
// on a master and to the master on a child
// (Child.Forwarding.Forwarder). That submission is where a master and a child part: the one place a
// set takes another way on each, while everything else here is the same for both. One of these for each
// entity type of an open database, shared by its sets.
internal sealed class SetOperations : IUpdateExecutor
{
    private readonly EngineDatabase _database;
    private readonly WriteCoordinator _writes;

    // The writes of a child, which go to its master; null on a master.
    private readonly Forwarder? _forwarder;

    private readonly PositionClock _clock;

    // How long an operation waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    // The read settings shared by the enumerations and searches of this entity type.
    private readonly ReadSettings _reads;
    private readonly ResidentViews _views;

    // Whether the entity type is stored with a List at any depth, whose bound a write is checked against
    // before it is submitted (RootValues.CheckCollectionBounds).
    private readonly bool _checksCollections;

    public SetOperations(EngineDatabase database, EntityTable table, WriteCoordinator writes, Forwarder? forwarder, PositionClock clock, LatticeDatabaseOptions options, ReadSettings reads, ResidentViews views)
    {
        _database = database;
        Table = table;
        _writes = writes;
        _forwarder = forwarder;
        _clock = clock;
        _synchronizationTimeout = options.SynchronizationTimeout;
        _reads = reads;
        _views = views;
        _checksCollections = table.Layout.Structure.HoldsCollections;
    }

    public EntityTable Table { get; }

    public LatticeUpdate CreateUpdate<TKey>(TKey key, IReadOnlyList<BoundProperty>? fields = null)
        where TKey : notnull
    {
        var normalized = FieldValues.NormalizeKey(Table.EntityType, key);
        _database.ThrowIfNotReady();
        return new LatticeUpdate(Table.EntityType, normalized, _database, this, fields);
    }

    public ValueTask<LatticeWriteResult> ExecuteUpdateAsync(object key, UpdateInput update, LatticeRevision? expectedRevision,
        LatticeSession? session, CancellationToken cancellationToken)
    {
        RootValues.BindUpdate(Table.Layout, update);
        var normalized = KeyArgument.OfNormalized(Table, key);
        return _forwarder is null
            ? _writes.SubmitUpdateAsync(Table, normalized, update, expectedRevision, session, cancellationToken)
            : _forwarder.WriteAsync(Table, WriteKindEnum.Update, normalized, values: null, update, expectedRevision, session, cancellationToken);
    }

    // A read by key: the copy inside the protection of reads, then the result built from it, outside
    // the protection, only when the entity exists. Null when it does not. The token of an asynchronous
    // read ends its wait for a slot of the protection (EntityTable.TryRead).
    public TResult? TryRead<TCopy, TResult>(in EntityKey key, TCopy copy, out LatticeRevision revision, out ulong sequence, CancellationToken cancellationToken = default)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        try
        {
            return Table.TryRead<TCopy, TResult>(key, ref copy, out revision, out sequence, cancellationToken) ? copy.Build() : null;
        }
        finally
        {
            copy.Return();
        }
    }

    // Waits first, reads second: the entity is not looked at before the position the session depends
    // on is published. The session moves to the position of the answer.
    public async ValueTask<LatticeReadResult<TResult>> ReadAsync<TCopy, TResult>(EntityKey key, TCopy copy, LatticeSession session, CancellationToken cancellationToken)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        await _clock.WaitAsync(session, _synchronizationTimeout, cancellationToken).ConfigureAwait(false);

        var result = TryRead<TCopy, TResult>(key, copy, out var revision, out var sequence, cancellationToken);
        var position = _clock.At(sequence);

        session.Advance(position);

        return new LatticeReadResult<TResult>(result, revision, position);
    }

    public bool TryView<TState, TResult>(in EntityKey key, TState state, LatticeViewFunc<LatticeRecordView, TState, TResult> viewer,
        [MaybeNullWhen(false)] out TResult result, out ulong sequence, CancellationToken cancellationToken = default) =>
        Table.TryView(key, _views, state, viewer, out result, out sequence, cancellationToken);

    public ValueTask<LatticeViewResult<TResult>> ViewAsync<TState, TResult>(EntityKey key, LatticeSession session,
        TState state, LatticeViewFunc<LatticeRecordView, TState, TResult> viewer, CancellationToken cancellationToken) =>
        ViewAsync(key, session, state, viewer, static (_, input) => input, cancellationToken);

    // The session wait and any schema binding take no epoch. The position of the answer
    // comes from the protected lookup, even if a publication advances the clock during the callback.
    public async ValueTask<LatticeViewResult<TResult>> ViewAsync<TInput, TState, TResult>(EntityKey key, LatticeSession session,
        TInput input, LatticeViewFunc<LatticeRecordView, TState, TResult> viewer, Func<LatticeEntityType, TInput, TState> prepare,
        CancellationToken cancellationToken)
    {
        await WaitAsync(session, cancellationToken).ConfigureAwait(false);

        var state = prepare(Table.EntityType, input);
        var found = TryView(key, state, viewer, out var result, out var sequence, cancellationToken);
        var position = _clock.At(sequence);
        session.Advance(position);
        return new LatticeViewResult<TResult>(found, result, position);
    }

    // The lazy enumeration of EntityTable.TryReadNext (Enumeration), a new copy for every item, each
    // built outside the protection of reads before it is returned.
    public IEnumerable<TResult> Enumerate<TCopy, TResult>(TCopy prototype)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class =>
        new Enumeration<TCopy, TResult>(this, prototype);

    // The same after the position the session depends on (SessionEnumeration).
    public IAsyncEnumerable<TResult> EnumerateAsync<TCopy, TResult>(TCopy prototype, LatticeSession session, CancellationToken cancellationToken)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class =>
        new SessionEnumeration<TCopy, TResult>(this, prototype, session, cancellationToken);

    // A new enumeration pass with the database's read limits.
    public EntityPass StartPass() => Table.StartPass(_reads);

    // A forward search for a lazy Find, with the database's read limits.
    public IndexSearch StartSearch(SecondaryIndex index, in IndexKey key) => new(_reads, index, key);

    // A search for FindAsync in the chosen direction, skipping Offset matches and applying the
    // database's read limits.
    public IndexSearch StartSearch(SecondaryIndex index, in IndexKey key, bool reverse, int offset) =>
        new(_reads, index, key, reverse, offset);

    // Waits for the position the session depends on, before the first step of an enumeration.
    public ValueTask WaitAsync(LatticeSession session, CancellationToken cancellationToken) =>
        _clock.WaitAsync(session, _synchronizationTimeout, cancellationToken);

    // The position of a sequence of the database.
    public LatticePosition At(ulong sequence) => _clock.At(sequence);

    // The next item of the pass, built from its copy outside the protection of reads and only then
    // committed to the pass, so that an item whose build throws is not taken as returned; null at the
    // end, with the sequence of the end. The token of an asynchronous enumeration is honored inside the
    // step (EntityTable.TryReadNext); the synchronous one gives none.
    public TResult? Next<TCopy, TResult>(EntityPass pass, TCopy copy, out ulong sequence, CancellationToken cancellationToken = default)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        try
        {
            if (!Table.TryReadNext<TCopy, TResult>(pass, ref copy, out sequence, cancellationToken))
            {
                return null;
            }

            var result = copy.Build();

            pass.Commit();

            return result;
        }
        finally
        {
            copy.Return();
        }
    }

    // The next entity of a search through the index, for the key bound to it, as Next takes the next of
    // an enumeration: built from its copy outside the protection of reads. The step gives the place past the
    // entity, which the search takes only once the entity is built, so that a build that throws leaves the
    // search before that entity and the next step reads it again, while what the step spent of the read
    // limits and a failure it recorded stay; null at the end, with the sequence of the end
    // (Entities.IndexSearch).
    public TResult? Next<TCopy, TResult>(ref IndexSearch search, SecondaryIndex index, in IndexKey key, TCopy copy, out ulong sequence, CancellationToken cancellationToken = default)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        try
        {
            if (!Table.TrySearch<TCopy, TResult>(index, key, ref search, ref copy, out sequence, out var next, cancellationToken))
            {
                return null;
            }

            var result = copy.Build();

            search.Commit(next, sequence);

            return result;
        }
        finally
        {
            copy.Return();
        }
    }

    // For FindAsync once it has returned its Limit: whether a match follows where the search stands
    // (Entities.EntityTable.SearchHasMore), with the sequence of that answer.
    public bool HasMore(ref IndexSearch search, SecondaryIndex index, in IndexKey key, out ulong sequence, CancellationToken cancellationToken) =>
        Table.SearchHasMore(index, key, ref search, out sequence, cancellationToken);

    // For FindAsync, which holds its answer until it returns: what its result holds grows by the bytes given,
    // which counts, once the search has a WalkProgress, against the memory limit of a read with what the
    // search remembers; before that, an answer of one entity whose step saw the end and that passed over no
    // match by Offset, nothing of it counts, as a read by key does not, and nothing is counted without a
    // memory limit. Over the limit the search fails with ReadLimitExceeded and lets go of what it remembers.
    // The check looks at the token first, as every look at a limit does (Entities.ReadLimits), so that a
    // cancellation is reported before the limit is, also one the code of the application made while it built
    // the entity just added.
    public void Hold(ref IndexSearch search, SecondaryIndex index, long bytes, CancellationToken cancellationToken)
    {
        if (search.Progress is not { } progress || !search.Limits.LimitsMemory)
        {
            return;
        }

        progress.Hold(bytes);
        search.CheckMemory(progress.Bytes + progress.Held, cancellationToken, Table.EntityType.Name, index.Definition.Name);
    }

    // Refuses a collection above its bound among the values a write took, with ValidationFailed
    // (RootValues.CheckCollectionBounds), for a set that validates them: before the write is submitted, and so
    // before its conditions, as the set refuses what the validation of the core refuses.
    public void CheckCollectionBounds(object?[] values)
    {
        if (_checksCollections)
        {
            RootValues.CheckCollectionBounds(Table.Layout, values);
        }
    }

    public ValueTask<LatticeWriteResult> WriteAsync(WriteKindEnum kind, EntityKey key, object?[]? values, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken) =>
        _forwarder is null
            ? _writes.SubmitAsync(Table, kind, key, values, expectedRevision, session, cancellationToken)
            : _forwarder.WriteAsync(Table, kind, key, values, update: null, expectedRevision, session, cancellationToken);
}
