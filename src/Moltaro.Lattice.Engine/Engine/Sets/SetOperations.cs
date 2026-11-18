using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Forwarding;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// What the set of a class and the set by name of one entity type do alike, for either copy: read
// with a session, enumerate with and without one, and submit a write: on a master to its write
// coordinator, on a child to its master (Forwarding.Forwarder). One of these for each entity type of
// an open database, shared by its sets.
internal sealed class SetOperations
{
    private readonly WriteCoordinator _writes;

    // The writes of a child, which go to its master; null on a master.
    private readonly Forwarder? _forwarder;

    private readonly PositionClock _clock;

    // How long an operation waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    // The read limits of the options, which every enumeration takes (EntityPass); null for none.
    private readonly long? _readMemoryLimit;

    private readonly TimeSpan? _readTimeLimit;

    public SetOperations(EntityTable table, WriteCoordinator writes, Forwarder? forwarder, PositionClock clock, LatticeDatabaseOptions options)
    {
        Table = table;
        _writes = writes;
        _forwarder = forwarder;
        _clock = clock;
        _synchronizationTimeout = options.SynchronizationTimeout;
        _readMemoryLimit = options.ReadMemoryLimit;
        _readTimeLimit = options.ReadTimeLimit;
    }

    public EntityTable Table { get; }

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
        await _clock.WaitAsync(session, _synchronizationTimeout, cancellationToken);

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
    public EntityPass StartPass() => Table.StartPass(_readMemoryLimit, _readTimeLimit);

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

    public ValueTask<LatticeWriteResult> WriteAsync(WriteKindEnum kind, EntityKey key, object?[]? values, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken) =>
        _forwarder is null
            ? _writes.SubmitAsync(Table, kind, key, values, expectedRevision, session, cancellationToken)
            : _forwarder.WriteAsync(Table, kind, key, values, expectedRevision, session, cancellationToken);
}
