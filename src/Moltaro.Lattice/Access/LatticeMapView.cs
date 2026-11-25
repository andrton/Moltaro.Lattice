namespace Moltaro.Lattice.Access;

/// <summary>A Map read in place for the duration of the entity's viewer.</summary>
public readonly ref struct LatticeMapView
{
    /// <summary>The number of key and value pairs.</summary>
    public int Count => throw new NotSupportedException();

    /// <summary>Finds an entry using the Map's key comparison and views its value.</summary>
    /// <typeparam name="TKey">The supplied key type. It binds by the rules of a search key.</typeparam>
    /// <param name="key">The key to find.</param>
    /// <param name="value">The value, valid for the duration of the viewer when the key is present.</param>
    /// <returns><see langword="true"/> when the key is present, including when its value is null.</returns>
    /// <exception cref="ArgumentException">The key is null or cannot bind to the Map's key declaration.</exception>
    public bool TryGetValue<TKey>(TKey key, out LatticeValueView value) => throw new NotSupportedException();

    /// <summary>Starts traversal of the entries for the duration of the viewer, with no order guaranteed.</summary>
    public Enumerator GetEnumerator() => throw new NotSupportedException();

    /// <summary>Traverses the Map while its view is valid.</summary>
    public ref struct Enumerator
    {
        /// <summary>The entry at the position of the most recent successful move.</summary>
        public readonly LatticeMapEntryView Current => throw new NotSupportedException();

        /// <summary>Advances to the next entry and reports whether there is one.</summary>
        public bool MoveNext() => throw new NotSupportedException();
    }
}
