namespace Moltaro.Lattice.Schema;

/// <summary>
/// A field of an entity type, as declared in the schema: its identity, its name and its
/// declaration.
/// </summary>
public sealed class LatticeField
{
    internal LatticeField(int id, string name, LatticeValueDeclaration declaration, int ordinal)
    {
        Id = id;
        Name = name;
        Declaration = declaration;
        Ordinal = ordinal;
    }

    /// <summary>The stable identity of the field within its entity type.</summary>
    public int Id { get; }

    /// <summary>
    /// The field name. Records are accessed by it; a .NET class bound to the entity type
    /// has a property with the same name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// What the field holds: its type, whether it accepts <see langword="null"/>, the
    /// comparison and storage of a String and the enum type of an Enum, and the .NET type
    /// of its values.
    /// </summary>
    public LatticeValueDeclaration Declaration { get; }

    // Position of the field's value inside a stored record.
    internal int Ordinal { get; }

    /// <summary>Returns the field name.</summary>
    public override string ToString() => Name;
}
