using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Sets;

// The enumeration of the entities of one entity type after the position a session depends on, as
// ILatticeSet<TEntity>.EnumerateAsync and ILatticeSet.EnumerateAsync return it: lazy, and every
// enumerator it gives is a pass of its own. Its first MoveNextAsync waits for the position of the
// session, outside every lock and outside the protection of reads; then each MoveNextAsync is one step
// (SetOperations.Next), which completes at once. The session moves forward with every item to the
// position it was read at, and at the end to the position under which the index showed no more, so
// it covers everything returned, also when nothing is.
//
// Written by hand for the reason Enumeration gives: a step that fails leaves the pass as it was, and
// every further MoveNextAsync is a step again, so the enumeration never ends as if it were complete
// after a failure. A wait that failed is waited for again. A failure comes as a faulted task, never as
// an exception thrown by MoveNextAsync itself.
//
// Cancellation, as an iterator of C# with an [EnumeratorCancellation] token takes it: the token given
// to EnumerateAsync and the one given to GetAsyncEnumerator both cancel, linked when both can; every
// step looks at it before it reads, and passes it on into the step, which honors it in its wait for a
// slot of the protection and between its rounds, before it looks at a limit (EntityTable.TryReadNext),
// with OperationCanceledException.
internal sealed class SessionEnumeration<TCopy, TResult> : IAsyncEnumerable<TResult>
    where TCopy : struct, IRootCopy<TResult>
    where TResult : class
{
    private readonly SetOperations _operations;

    private readonly TCopy _prototype;

    private readonly LatticeSession _session;

    private readonly CancellationToken _cancellationToken;

    public SessionEnumeration(SetOperations operations, TCopy prototype, LatticeSession session, CancellationToken cancellationToken)
    {
        _operations = operations;
        _prototype = prototype;
        _session = session;
        _cancellationToken = cancellationToken;
    }

    public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(this, cancellationToken);

    private sealed class Enumerator : IAsyncEnumerator<TResult>
    {
        private readonly SetOperations _operations;

        private readonly TCopy _prototype;

        private readonly LatticeSession _session;

        // The source of the two tokens linked, when both can cancel; disposed with the enumerator.
        private readonly CancellationTokenSource? _linked;

        private readonly CancellationToken _cancellationToken;

        // Null once the pass has ended or the enumerator was disposed.
        private EntityPass? _pass;

        // Whether the position of the session has been reached.
        private bool _waited;

        private TResult? _current;

        public Enumerator(SessionEnumeration<TCopy, TResult> enumeration, CancellationToken cancellationToken)
        {
            _operations = enumeration._operations;
            _prototype = enumeration._prototype;
            _session = enumeration._session;
            _pass = _operations.StartPass();

            var own = enumeration._cancellationToken;

            if (!cancellationToken.CanBeCanceled || cancellationToken == own)
            {
                _cancellationToken = own;
            }
            else if (!own.CanBeCanceled)
            {
                _cancellationToken = cancellationToken;
            }
            else
            {
                _linked = CancellationTokenSource.CreateLinkedTokenSource(own, cancellationToken);
                _cancellationToken = _linked.Token;
            }
        }

        public TResult Current => _current!;

        public ValueTask<bool> MoveNextAsync()
        {
            if (_pass is null)
            {
                return ValueTask.FromResult(false);
            }

            try
            {
                if (!_waited)
                {
                    var wait = _operations.WaitAsync(_session, _cancellationToken);

                    if (!wait.IsCompletedSuccessfully)
                    {
                        return StepAfterAsync(wait);
                    }

                    _waited = true;
                }

                return ValueTask.FromResult(Step());
            }
            catch (Exception exception)
            {
                return ValueTask.FromException<bool>(exception);
            }
        }

        public ValueTask DisposeAsync()
        {
            _pass = null;
            _current = null;
            _linked?.Dispose();

            return ValueTask.CompletedTask;
        }

        private async ValueTask<bool> StepAfterAsync(ValueTask wait)
        {
            await wait.ConfigureAwait(false);
            _waited = true;

            return Step();
        }

        private bool Step()
        {
            _cancellationToken.ThrowIfCancellationRequested();

            var result = _operations.Next<TCopy, TResult>(_pass!, _prototype, out var sequence, _cancellationToken);

            _session.Advance(_operations.At(sequence));

            if (result is null)
            {
                _pass = null;
                _current = null;

                return false;
            }

            _current = result;

            return true;
        }
    }
}
