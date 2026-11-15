using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// What the set of a class and the set by name of one entity type do alike, for either copy: read
// with a session, enumerate with and without one, and submit a write. One of these for each
// entity type of an open database, shared by its sets.
internal sealed class SetOperations
{
    private readonly WriteExecutor _writes;

    private readonly PositionClock _clock;

    // How long an operation waits for the position of its session when the session does not say.
    private readonly TimeSpan _synchronizationTimeout;

    public SetOperations(EntityTable table, WriteExecutor writes, PositionClock clock, TimeSpan synchronizationTimeout)
    {
        Table = table;
        _writes = writes;
        _clock = clock;
        _synchronizationTimeout = synchronizationTimeout;
    }

    public EntityTable Table { get; }

    // A read by key: the copy inside the read gate, then the result built from it, outside the gate,
    // only when the entity exists. Null when it does not.
    public TResult? TryRead<TCopy, TResult>(in EntityKey key, TCopy copy, out LatticeRevision revision, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        try
        {
            return Table.TryRead<TCopy, TResult>(key, ref copy, out revision, out sequence) ? copy.Build() : null;
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

        var result = TryRead<TCopy, TResult>(key, copy, out var revision, out var sequence);
        var position = _clock.At(sequence);

        session.Advance(position);

        return new LatticeReadResult<TResult>(result, revision, position);
    }

    // The lazy enumeration of EntityTable.TryReadNext, a new copy for every item, each built outside
    // the read gate before it is returned.
    public IEnumerable<TResult> Enumerate<TCopy, TResult>(TCopy prototype)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        var cursor = new EntityCursor();

        while (true)
        {
            var result = TryReadNext<TCopy, TResult>(ref cursor, prototype, out _);

            if (result is null)
            {
                yield break;
            }

            yield return result;
        }
    }

    // The same after the position the session depends on. The session moves forward with every item
    // to the position it was read at, and at the end to the position under which the index showed no
    // more, so it covers everything returned, also when nothing is.
    public async IAsyncEnumerable<TResult> EnumerateAsync<TCopy, TResult>(TCopy prototype, LatticeSession session, [EnumeratorCancellation] CancellationToken cancellationToken)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        await _clock.WaitAsync(session, _synchronizationTimeout, cancellationToken);

        var cursor = new EntityCursor();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = TryReadNext<TCopy, TResult>(ref cursor, prototype, out var sequence);

            session.Advance(_clock.At(sequence));

            if (result is null)
            {
                yield break;
            }

            yield return result;
        }
    }

    // The next item of an enumeration, built from its copy outside the read gate; null at the end.
    private TResult? TryReadNext<TCopy, TResult>(ref EntityCursor cursor, TCopy copy, out ulong sequence)
        where TCopy : struct, IRootCopy<TResult>
        where TResult : class
    {
        try
        {
            return Table.TryReadNext<TCopy, TResult>(ref cursor, ref copy, out sequence) ? copy.Build() : null;
        }
        finally
        {
            copy.Return();
        }
    }

    public ValueTask<LatticeWriteResult> WriteAsync(WriteKindEnum kind, EntityKey key, object?[]? values, LatticeRevision? expectedRevision, LatticeSession? session, CancellationToken cancellationToken) =>
        _writes.SubmitAsync(Table, kind, key, values, expectedRevision, session, cancellationToken);
}
