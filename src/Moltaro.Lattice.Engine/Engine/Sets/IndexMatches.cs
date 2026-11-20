using System.Collections;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Indexes;

namespace Moltaro.Lattice.Engine.Sets;

// The entities a search through an index finds without a session, as ILatticeIndex<TEntity>.Find returns
// them: lazy, and one object that is both the enumerable and the enumerator its first GetEnumerator gives,
// holding the key, bound to the fields of the index, once, and where the search stands between two steps
// (Entities.IndexSearch). A search that returns one entity so allocates this object and the entity. The
// first enumerator is taken once, by an interlocked exchange, so that an enumerable that several threads
// share gives each a search of its own; every further GetEnumerator is a new object, a search of its own,
// which takes its first step at its first MoveNext. A MoveNext is one step (SetOperations.Next): it reads
// the next entity of the key inside the protection of reads and builds it outside.
//
// Written by hand for the reason Enumeration gives: a step that throws leaves the search as it was, and
// every further MoveNext is a step again, so a search never ends as if it were complete after a failure.
// After the close every step throws ObjectDisposedException. MoveNext returns false only at the end of
// the search, and after Dispose, which lets the search go. Between two steps the object holds the search
// and nothing of the database holds the object.
internal sealed class IndexMatches<TCopy, TResult> : IEnumerable<TResult>, IEnumerator<TResult>
    where TCopy : struct, IRootCopy<TResult>
    where TResult : class
{
    // Made and not enumerated yet; enumerated, as its own first enumerator or as a later one; ended or
    // disposed.
    private const int Made = 0;

    private const int Enumerating = 1;

    private const int Ended = 2;

    private readonly SetOperations _operations;

    // Null when the engine does not store the entity type.
    private readonly SecondaryIndex? _index;

    // The key, bound to the fields of the index, the one copy of it the search holds.
    private readonly IndexKey _key;

    private readonly TCopy _prototype;

    private IndexSearch _search;

    private TResult? _current;

    private int _state;

    public IndexMatches(SetOperations operations, SecondaryIndex? index, in IndexKey key, TCopy prototype)
    {
        _operations = operations;
        _index = index;
        _key = key;
        _prototype = prototype;
    }

    // The enumerator of a search of its own, which the constructor of a later one makes ready.
    private IndexMatches(IndexMatches<TCopy, TResult> matches)
        : this(matches._operations, matches._index, matches._key, matches._prototype)
    {
        _state = Enumerating;
    }

    public TResult Current => _current!;

    object IEnumerator.Current => Current;

    public IEnumerator<TResult> GetEnumerator() =>
        Interlocked.CompareExchange(ref _state, Enumerating, Made) == Made ? this : new IndexMatches<TCopy, TResult>(this);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool MoveNext()
    {
        if (_state != Enumerating)
        {
            return false;
        }

        var result = _operations.Next<TCopy, TResult>(ref _search, _index, _key, _prototype, out _);

        if (result is null)
        {
            _state = Ended;
            _current = null;

            return false;
        }

        _current = result;

        return true;
    }

    public void Reset() => throw new NotSupportedException("A search through an index cannot be reset; start a new one.");

    public void Dispose()
    {
        _state = Ended;
        _current = null;
    }
}
