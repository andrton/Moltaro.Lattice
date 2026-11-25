namespace Moltaro.Lattice.Access;

/// <summary>A Map entry read in place for the duration of the entity's viewer.</summary>
public readonly ref struct LatticeMapEntryView
{
    /// <summary>The stored key, preserving the spelling of a text key.</summary>
    public LatticeValueView Key => throw new NotSupportedException();

    /// <summary>The value associated with the key, which may hold null.</summary>
    public LatticeValueView Value => throw new NotSupportedException();

    /// <summary>Views both the stored key and its value for the duration of the viewer.</summary>
    /// <param name="key">The stored key.</param>
    /// <param name="value">The value associated with the key.</param>
    public void Deconstruct(out LatticeValueView key, out LatticeValueView value) => throw new NotSupportedException();
}
