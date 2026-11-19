using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Indexes;

namespace Moltaro.Lattice.Engine.Sets;

// The entities a search through an index finds without a session, as ILatticeIndex<TEntity>.Find returns
// them: lazy, and every enumerator it gives is a search of its own (Entities.IndexSearch), which takes its
// first step at its first MoveNext. A MoveNext is one step (SetOperations.Next): it reads the next entity
// of the key inside the protection of reads and builds it outside.
//
// Written by hand for the reason Enumeration gives: a step that throws leaves the search as it was, and
// every further MoveNext is a step again, so a search never ends as if it were complete after a failure.
// After the close every step throws ObjectDisposedException. MoveNext returns false only at the end of
// the search, and after Dispose, which lets the search go. Between two steps an enumerator holds the
// search and nothing of the database holds the enumerator.
internal sealed class IndexMatches<TCopy, TResult> : IEnumerable<TResult>
    where TCopy : struct, IRootCopy<TResult>
    where TResult : class
{
    private readonly SetOperations _operations;

    private readonly SecondaryIndex? _index;

    // The key, bound to the fields of the index.
    private readonly IndexKey _key;

    private readonly TCopy _prototype;

    public IndexMatches(SetOperations operations, SecondaryIndex? index, IndexKey key, TCopy prototype)
    {
        _operations = operations;
        _index = index;
        _key = key;
        _prototype = prototype;
    }

    public IEnumerator<TResult> GetEnumerator() => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class Enumerator : IEnumerator<TResult>
    {
        private readonly SetOperations _operations;

        private readonly TCopy _prototype;

        // Null once the search has ended or the enumerator was disposed.
        private IndexSearch? _search;

        private TResult? _current;

        public Enumerator(IndexMatches<TCopy, TResult> matches)
        {
            _operations = matches._operations;
            _prototype = matches._prototype;
            _search = new IndexSearch(matches._operations.Table, matches._index, matches._key);
        }

        public TResult Current => _current!;

        object IEnumerator.Current => Current;

        public bool MoveNext()
        {
            if (_search is null)
            {
                return false;
            }

            var result = _operations.Next<TCopy, TResult>(_search, _prototype, out _);

            if (result is null)
            {
                _search = null;
                _current = null;

                return false;
            }

            _current = result;

            return true;
        }

        public void Reset() => throw new NotSupportedException("A search through an index cannot be reset; start a new one.");

        public void Dispose()
        {
            _search = null;
            _current = null;
        }
    }
}
