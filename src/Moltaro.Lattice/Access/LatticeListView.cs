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
        private ViewPackedBlock _block;
        private int _ordinal;
        private int _blockOrdinal;
        private int _offset;

        internal Enumerator(LatticeListView view)
        {
            _view = view;
            _block = view.Count == 0 ? default : view._storage.ReadPackedListBlock(view._collection, 0);
            _ordinal = -1;
            _blockOrdinal = 0;
            _offset = -_block.Stride;
        }

        /// <summary>The element at the position of the most recent successful move.</summary>
        /// <exception cref="InvalidOperationException">There is no current element.</exception>
        public readonly LatticeValueView Current
        {
            get
            {
                _view._collection.RequireCurrent(_ordinal);
                var stride = _block.Stride;
                if (stride == 0 || _block.Bytes.Length - _offset < stride)
                {
                    // A split record follows the same two-part read as the indexer.
                    return _view.ElementAt(_ordinal);
                }

                var row = new ViewRecord(_view._collection.EntryLayout, _block.Bytes.Slice(_offset, stride), default, 0);
                return new LatticeValueView(_view._storage, row, 0, _view._element);
            }
        }

        /// <summary>Advances to the next element and reports whether there is one.</summary>
        public bool MoveNext()
        {
            if (!_view._collection.MoveNext(ref _ordinal))
            {
                return false;
            }

            var stride = _block.Stride;
            if (stride != 0)
            {
                _offset += stride;
                if (_offset >= _block.Bytes.Length)
                {
                    _offset -= _block.Bytes.Length;
                    _blockOrdinal++;
                    _block = _view._storage.ReadPackedListBlock(_view._collection, _blockOrdinal);
                }
            }

            return true;
        }
    }
}
