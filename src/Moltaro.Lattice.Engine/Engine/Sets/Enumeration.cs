using System.Collections;
using Moltaro.Lattice.Engine.Entities;

namespace Moltaro.Lattice.Engine.Sets;

// The enumeration of the entities of one entity type without a session, as ILatticeSet<TEntity>.Enumerate
// and ILatticeSet.Enumerate return it: lazy, and every enumerator it gives is a pass of its own, which
// takes its first step at its first MoveNext. A MoveNext is one step (SetOperations.Next): it reads the
// next entity inside the protection of reads and builds it outside.
//
// Written by hand rather than as an iterator of C#, because an iterator ends after the first exception
// it meets, and its next MoveNext returns false as if the enumeration had ended. Here a step that throws
// leaves the pass as it was, and every further MoveNext is a step again: after the close it throws
// ObjectDisposedException as the first did, and after the code of the application failed to build an
// entity, the step reads that entity again. So an enumeration never ends as if it were complete after
// a failure. MoveNext returns false only at the end of the pass, and after Dispose, which lets the
// pass go.
internal sealed class Enumeration<TCopy, TResult> : IEnumerable<TResult>
    where TCopy : struct, IRootCopy<TResult>
    where TResult : class
{
    private readonly SetOperations _operations;

    private readonly TCopy _prototype;

    public Enumeration(SetOperations operations, TCopy prototype)
    {
        _operations = operations;
        _prototype = prototype;
    }

    public IEnumerator<TResult> GetEnumerator() => new Enumerator(_operations, _prototype);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Enumerator : IEnumerator<TResult>
    {
        private readonly SetOperations _operations;

        private readonly TCopy _prototype;

        // Null once the pass has ended or the enumerator was disposed.
        private EntityPass? _pass;

        private TResult? _current;

        public Enumerator(SetOperations operations, TCopy prototype)
        {
            _operations = operations;
            _prototype = prototype;
            _pass = operations.StartPass();
        }

        public TResult Current => _current!;

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_pass is null)
            {
                return false;
            }

            var result = _operations.Next<TCopy, TResult>(_pass, _prototype, out _);

            if (result is null)
            {
                _pass = null;
                _current = null;

                return false;
            }

            _current = result;

            return true;
        }

        public void Reset() => throw new NotSupportedException("An enumeration of entities cannot be reset; start a new one.");

        public void Dispose()
        {
            _pass = null;
            _current = null;
        }
    }
}
