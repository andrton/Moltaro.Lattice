using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>A Map read in place for the duration of the entity's viewer.</summary>
public readonly ref struct LatticeMapView
{
    private readonly IViewStorage _storage;
    private readonly ViewCollection _collection;
    private readonly LatticeValueDeclaration _declaration;

    internal LatticeMapView(IViewStorage storage, ViewCollection collection, LatticeValueDeclaration declaration)
    {
        _storage = storage;
        _collection = collection;
        _declaration = declaration;
    }

    /// <summary>The number of key and value pairs.</summary>
    public int Count => _collection.Count;

    /// <summary>Finds an entry using the Map's key comparison and views its value.</summary>
    /// <typeparam name="TKey">The supplied key type. It binds by the rules of a search key.</typeparam>
    /// <param name="key">The key to find.</param>
    /// <param name="value">The value, valid for the duration of the viewer when the key is present.</param>
    /// <returns><see langword="true"/> when the key is present, including when its value is null.</returns>
    /// <exception cref="ArgumentException">The key is null or cannot bind to the Map's key declaration.</exception>
    public bool TryGetValue<TKey>(TKey key, out LatticeValueView value)
    {
        var supplied = IndexValue.Of(key);
        if (supplied.IsNull)
        {
            throw new ArgumentException("A Map key cannot be null.", nameof(key));
        }

        var probe = supplied.Bind(_declaration.Key!, "Map key", nameof(key));
        var ordinal = _storage.FindEntry(_collection, probe);
        if (ordinal < 0)
        {
            value = default;
            return false;
        }

        value = new LatticeValueView(_storage, _storage.Entry(_collection, ordinal), 1, _declaration.Value!);
        return true;
    }

    /// <summary>Starts traversal of the entries for the duration of the viewer, with no order guaranteed.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Traverses the Map while its view is valid.</summary>
    public ref struct Enumerator
    {
        private readonly LatticeMapView _view;
        private int _ordinal;

        internal Enumerator(LatticeMapView view)
        {
            _view = view;
            _ordinal = -1;
        }

        /// <summary>The entry at the position of the most recent successful move.</summary>
        /// <exception cref="InvalidOperationException">There is no current entry.</exception>
        public readonly LatticeMapEntryView Current
        {
            get
            {
                _view._collection.RequireCurrent(_ordinal);
                var row = _view._storage.Entry(_view._collection, _ordinal);
                return new LatticeMapEntryView(_view._storage, row, _view._declaration);
            }
        }

        /// <summary>Advances to the next entry and reports whether there is one.</summary>
        public bool MoveNext() => _view._collection.MoveNext(ref _ordinal);
    }
}
