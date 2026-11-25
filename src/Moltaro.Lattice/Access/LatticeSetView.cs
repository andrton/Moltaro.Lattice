using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>A Set read in place for the duration of the entity's viewer.</summary>
public readonly ref struct LatticeSetView
{
    private readonly IViewStorage _storage;
    private readonly ViewCollection _collection;
    private readonly LatticeValueDeclaration _element;

    internal LatticeSetView(IViewStorage storage, ViewCollection collection, LatticeValueDeclaration element)
    {
        _storage = storage;
        _collection = collection;
        _element = element;
    }

    /// <summary>The number of members, including a null member when present.</summary>
    public int Count => _collection.Count;

    /// <summary>Tests for membership using the Set's comparison.</summary>
    /// <typeparam name="TMember">The supplied member type. It binds by the rules of a search key.</typeparam>
    /// <param name="member">The member to find. Null is allowed with a compatible nullable declaration.</param>
    /// <returns><see langword="true"/> when the Set contains the member.</returns>
    /// <exception cref="ArgumentException">The member cannot bind to the Set's member declaration.</exception>
    public bool Contains<TMember>(TMember member)
    {
        var supplied = IndexValue.Of(member);
        if (supplied.IsNull && !_element.IsNullable)
        {
            throw new ArgumentException("The Set does not allow null members.", nameof(member));
        }

        var probe = supplied.Bind(_element, "Set member", nameof(member));
        return _storage.FindEntry(_collection, probe) >= 0;
    }

    /// <summary>Starts traversal of the members for the duration of the viewer, with no order guaranteed.</summary>
    public Enumerator GetEnumerator() => new(this);

    /// <summary>Traverses the Set while its view is valid.</summary>
    public ref struct Enumerator
    {
        private readonly LatticeSetView _view;
        private int _ordinal;

        internal Enumerator(LatticeSetView view)
        {
            _view = view;
            _ordinal = -1;
        }

        /// <summary>The member at the position of the most recent successful move.</summary>
        /// <exception cref="InvalidOperationException">There is no current member.</exception>
        public readonly LatticeValueView Current
        {
            get
            {
                _view._collection.RequireCurrent(_ordinal);
                var row = _view._storage.Entry(_view._collection, _ordinal);
                return new LatticeValueView(_view._storage, row, 0, _view._element);
            }
        }

        /// <summary>Advances to the next member and reports whether there is one.</summary>
        public bool MoveNext() => _view._collection.MoveNext(ref _ordinal);
    }
}
