using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Child.Forwarding;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Writes;

namespace Moltaro.Lattice.Engine.Sets;

// What the set of a class and the set by name of one entity type do alike, for either copy: read
// with a session, enumerate with and without one, take the steps of a search through an index, and
// submit a write: on a master to its write coordinator, on a child to its master
// (Child.Forwarding.Forwarder). That submission is where a master and a child part: the one place a
// set takes another way on each, while everything else here is the same for both. One of these for each
// entity type of an open database, shared by its sets.
internal sealed class SetOperations
{
    private readonly WriteCoordinator _writes;

    // The writes of a child, which go to its master; null on a master.
    private readonly Forwarder? _forwarder;

    private readonly PositionClock _clock;

    // How long an operation waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    // The options of the database, whose read limits every enumeration and every search takes
    // (Entities.ReadLimits).
    private readonly LatticeDatabaseOptions _options;

    public SetOperations(EntityTable table, WriteCoordinator writes, Forwarder? forwarder, PositionClock clock, LatticeDatabaseOptions options)
    {
        Table = table;
        _writes = writes;
        _forwarder = forwarder;
        _clock = clock;
        _synchronizationTimeout = options.SynchronizationTimeout;
        _options = options;
    }

    public EntityTable Table { get; }

    // The options of the database, whose read limits a search takes (Entities.IndexSearch).
    public LatticeDatabaseOptions Options => _options;

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

    // A new pass of an enumeration of the entity type, under the read limits of the options.
    public EntityPass StartPass() => Table.StartPass(_options);

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
    // an enumeration: built from its copy outside the protection of reads. The step moves the search past
    // the entity, and the place it stood at before is kept here and put back when the build throws, so that
    // the next step reads that entity again, while what the step spent of the read limits and a failure it
    // recorded stay; null at the end, with the sequence of the end (Entities.IndexSearch).
    public TResult? Next<TCopy, TResult>(ref IndexSearch search, SecondaryIndex? index, in IndexKey key, TCopy copy, out ulong sequence, CancellationToken cancellationToken = default)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        var place = search.Place;

        try
        {
            if (!Table.TrySearch<TCopy, TResult>(index, key, ref search, ref copy, out sequence, cancellationToken))
            {
                return null;
            }

            TResult result;

            try
            {
                result = copy.Build();
            }
            catch
            {
                search.PutBack(place);

                throw;
            }

            search.Commit();

            return result;
        }
        finally
        {
            copy.Return();
        }
    }

    // For FindAsync once it has returned its Limit: whether a match follows where the search stands
    // (Entities.EntityTable.SearchHasMore), with the sequence of that answer.
    public bool HasMore(ref IndexSearch search, SecondaryIndex? index, in IndexKey key, out ulong sequence, CancellationToken cancellationToken) =>
        Table.SearchHasMore(index, key, ref search, out sequence, cancellationToken);

    // For FindAsync, until it returns: what its result holds grew by the bytes given, which counts, once the
    // search has a WalkProgress, against the memory limit of a read with what the search remembers; over
    // the limit the search fails with ReadLimitExceeded and lets go of what it remembers.
    public void Hold(ref IndexSearch search, SecondaryIndex? index, long bytes)
    {
        search.Hold(bytes);

        if (search.Progress is not { } progress || !search.HoldsAnswer || !search.Limits.LimitsMemory)
        {
            return;
        }

        try
        {
            search.Limits.CheckMemory(progress.Bytes + progress.Held, Table.EntityType.Name, index?.Definition.Name);
        }
        catch (LatticeException)
        {
            progress.Clear();

            throw;
        }
    }

    public ValueTask<LatticeWriteResult> WriteAsync(WriteKindEnum kind, EntityKey key, object?[]? values, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken) =>
        _forwarder is null
            ? _writes.SubmitAsync(Table, kind, key, values, expectedRevision, session, cancellationToken)
            : _forwarder.WriteAsync(Table, kind, key, values, expectedRevision, session, cancellationToken);
}
