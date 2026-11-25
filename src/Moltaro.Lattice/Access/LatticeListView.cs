namespace Moltaro.Lattice.Access;

/// <summary>A List read in place for the duration of the entity's viewer.</summary>
public readonly ref struct LatticeListView
{
    /// <summary>The number of elements, including null elements.</summary>
    public int Count => throw new NotSupportedException();

    /// <summary>Views the element at the given position.</summary>
    /// <param name="index">The zero-based position in the List.</param>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the List.</exception>
    public LatticeValueView this[int index] => throw new NotSupportedException();

    /// <summary>Starts traversal of the elements in List order for the duration of the viewer.</summary>
    public Enumerator GetEnumerator() => throw new NotSupportedException();

    /// <summary>Traverses the List while its view is valid.</summary>
    public ref struct Enumerator
    {
        /// <summary>The element at the position of the most recent successful move.</summary>
        public readonly LatticeValueView Current => throw new NotSupportedException();

        /// <summary>Advances to the next element and reports whether there is one.</summary>
        public bool MoveNext() => throw new NotSupportedException();
    }
}
