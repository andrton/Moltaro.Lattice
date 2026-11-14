namespace Moltaro.Lattice.Schema;

/// <summary>
/// A field of an entity type, as declared in the schema.
/// </summary>
public sealed class LatticeField
{
    internal LatticeField(int id, string name, LatticeFieldTypeEnum type, bool isNullable, int ordinal)
    {
        Id = id;
        Name = name;
        Type = type;
        IsNullable = isNullable;
        Ordinal = ordinal;
    }

    /// <summary>The stable identity of the field within its entity type.</summary>
    public int Id { get; }

    /// <summary>
    /// The field name. Records are accessed by it; a .NET class bound to the entity type
    /// has a property with the same name.
    /// </summary>
    public string Name { get; }

    /// <summary>The declared type.</summary>
    public LatticeFieldTypeEnum Type { get; }

    /// <summary>Whether the field accepts <see langword="null"/>.</summary>
    public bool IsNullable { get; }

    /// <summary>The .NET type of a non-null value of the field.</summary>
    public Type ValueType => FieldTypes.ValueType(Type);

    // Position of the field's value inside a stored record.
    internal int Ordinal { get; }

    /// <summary>Returns the field name.</summary>
    public override string ToString() => Name;
}
