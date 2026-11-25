namespace Moltaro.Lattice.Access;

/// <summary>A Set read in place for the duration of the entity's viewer.</summary>
public readonly ref struct LatticeSetView
{
    /// <summary>The number of members, including a null member when present.</summary>
    public int Count => throw new NotSupportedException();

    /// <summary>Tests for membership using the Set's comparison.</summary>
    /// <typeparam name="TMember">The supplied member type. It binds by the rules of a search key.</typeparam>
    /// <param name="member">The member to find. Null is allowed with a compatible nullable declaration.</param>
    /// <returns><see langword="true"/> when the Set contains the member.</returns>
    /// <exception cref="ArgumentException">The member cannot bind to the Set's member declaration.</exception>
    public bool Contains<TMember>(TMember member) => throw new NotSupportedException();

    /// <summary>Starts traversal of the members for the duration of the viewer, with no order guaranteed.</summary>
    public Enumerator GetEnumerator() => throw new NotSupportedException();

    /// <summary>Traverses the Set while its view is valid.</summary>
    public ref struct Enumerator
    {
        /// <summary>The member at the position of the most recent successful move.</summary>
        public readonly LatticeValueView Current => throw new NotSupportedException();

        /// <summary>Advances to the next member and reports whether there is one.</summary>
        public bool MoveNext() => throw new NotSupportedException();
    }
}
