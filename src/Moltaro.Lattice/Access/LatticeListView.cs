using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>A List read in place for the duration of the entity's viewer.</summary>
public readonly ref struct LatticeListView
{
    private readonly IViewStorage _storage;
    private readonly ViewCollection _collection;
    private readonly LatticeValueDeclaration _element;

    internal LatticeListView(IViewStorage storage, ViewCollection collection, LatticeValueDeclaration element)
    {
        _storage = storage;
        _collection = collection;
        _element = element;
    }

    /// <summary>The number of elements, including null elements.</summary>
    public int Count => _collection.Count;

    /// <summary>Views the element at the given position.</summary>
    /// <param name="index">The zero-based position in the List.</param>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the List.</exception>
    public LatticeValueView this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return ElementAt(index);
        }
    }

    private LatticeValueView ElementAt(int index) =>
        new(_storage, _storage.Entry(_collection, index), 0, _element);

    /// <summary>Starts traversal of the elements in List order for the duration of the viewer.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Traverses the List while its view is valid.</summary>
    public ref struct Enumerator
    {
        private readonly LatticeListView _view;
        private int _ordinal;

        internal Enumerator(LatticeListView view)
        {
            _view = view;
            _ordinal = -1;
        }

        /// <summary>The element at the position of the most recent successful move.</summary>
        /// <exception cref="InvalidOperationException">There is no current element.</exception>
        public readonly LatticeValueView Current
        {
            get
            {
                _view._collection.RequireCurrent(_ordinal);
                return _view.ElementAt(_ordinal);
            }
        }

        /// <summary>Advances to the next element and reports whether there is one.</summary>
        public bool MoveNext() => _view._collection.MoveNext(ref _ordinal);
    }
}
