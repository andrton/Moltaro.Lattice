using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Schema;

/// <summary>
/// An owned type declared in the schema: the fields of a value that belongs to the entity that
/// holds it, and optionally the .NET class that represents such a value. A field of type
/// <see cref="LatticeFieldTypeEnum.Owned"/> names it.
/// </summary>
/// <remarks>
/// <para>
/// A value of an owned type has no key and no identity of its own. It belongs to one entity,
/// is read and written with that entity, and is never shared with another one. It can be held
/// by a field of an entity type or of another owned type, by an element of a List and by a
/// value of a Map; never by a Map key or a Set element.
/// </para>
/// <para>
/// <b>Not implemented yet.</b> An owned type is declared, compiled, saved and loaded, records
/// hold its values as <c>LatticeOwnedValue</c> and its class is bound against it, but storing
/// its values is not implemented: every write of an entity type that has an Owned field throws
/// <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class LatticeOwnedType
{
    private readonly Dictionary<string, LatticeField> _fieldsByName;

    internal LatticeOwnedType(int id, string name, string? clrTypeName, IReadOnlyList<LatticeField> fields)
    {
        Id = id;
        Name = name;
        ClrTypeName = clrTypeName;
        Fields = fields;
        _fieldsByName = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
    }

    /// <summary>The stable identity of the owned type within the schema.</summary>
    public int Id { get; }

    /// <summary>
    /// The owned type name, unique within the schema. A field that holds a value of the owned
    /// type names it.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The full name of the .NET class that represents values of this owned type, such as
    /// <c>MyApp.Address</c>: the type of the property of a bound class that holds such a
    /// value. Optional: <see langword="null"/> when the values are accessed only through
    /// records.
    /// </summary>
    public string? ClrTypeName { get; }

    /// <summary>All fields in declaration order.</summary>
    public IReadOnlyList<LatticeField> Fields { get; }

    /// <summary>Finds a field by name.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="field">The field, when found.</param>
    /// <returns><see langword="true"/> when the owned type has the field.</returns>
    public bool TryGetField(string name, [NotNullWhen(true)] out LatticeField? field) =>
        _fieldsByName.TryGetValue(name, out field);
}
