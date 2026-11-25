using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>A Map entry read in place for the duration of the entity's viewer.</summary>
public readonly ref struct LatticeMapEntryView
{
    private readonly IViewStorage _storage;
    private readonly ViewRecord _row;
    private readonly LatticeValueDeclaration _declaration;

    internal LatticeMapEntryView(IViewStorage storage, ViewRecord row, LatticeValueDeclaration declaration)
    {
        _storage = storage;
        _row = row;
        _declaration = declaration;
    }

    /// <summary>The stored key, preserving the spelling of a text key.</summary>
    public LatticeValueView Key => new(_storage, _row, 0, _declaration.Key!);

    /// <summary>The value associated with the key, which may hold null.</summary>
    public LatticeValueView Value => new(_storage, _row, 1, _declaration.Value!);

    /// <summary>Views both the stored key and its value for the duration of the viewer.</summary>
    /// <param name="key">The stored key.</param>
    /// <param name="value">The value associated with the key.</param>
    public void Deconstruct(out LatticeValueView key, out LatticeValueView value)
    {
        key = Key;
        value = Value;
    }
}
