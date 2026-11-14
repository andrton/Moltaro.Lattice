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
/// An owned type does not contain itself. What it contains is what its values own: the owned
/// types of its Owned fields, of the elements of its Lists and of the values of its Maps,
/// also through collections inside collections, and whatever those owned types contain in
/// turn. A declaration in which this leads from an owned type back to itself is refused with
/// <see cref="LatticeErrorEnum.InvalidSchema"/>, which names the owned types on the way. The
/// shortest case, a field of its own type, is refused whether the field is nullable or not.
/// A Reference is not containment: an owned type may refer to any entity type, the one whose
/// entities hold it included.
/// </para>
/// <para>
/// <b>Not implemented yet.</b> An owned type is declared, compiled, saved and loaded, records
/// hold its values as <c>LatticeOwnedValue</c> and its class is bound against it, but storing
/// its values is not implemented. Until then no write of an entity type that has an Owned
/// field is carried out: after the checks every write makes of its arguments and of the
/// entity against the schema, it throws <see cref="NotSupportedException"/>.
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

    /// <summary>
    /// The stable identity of the owned type, unique among the owned types of the schema.
    /// Owned types are a scope of their own: an entity type or an enum type may have the same
    /// identity. A second owned type with the identity is refused with
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>, with the place named.
    /// </summary>
    public int Id { get; }

    /// <summary>
    /// The owned type name, unique among the owned types of the schema. A field that holds a
    /// value of the owned type names it. An entity type or an enum type may have the same
    /// name, because a declaration names a type of one kind. A second owned type with the name
    /// is refused with <see cref="LatticeErrorEnum.InvalidSchema"/>, with the place named.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The full name of the .NET class that represents values of this owned type, such as
    /// <c>MyApp.Address</c>: the class that a bound class holds them as, see
    /// <see cref="ILatticeDatabase.Set{TEntity}"/>. A .NET class is named by at most one type
    /// of the schema: another owned type or an entity type that names the same class is
    /// refused with <see cref="LatticeErrorEnum.InvalidSchema"/>, with the class named.
    /// Optional: without it the values are accessed through records only, and a class with a
    /// field of this owned type at any position does not bind. When the descriptor gives a
    /// <c>ClrType</c>, it is not empty: an empty one is refused with
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>, with the owned type named.
    /// </summary>
    public string? ClrTypeName { get; }

    /// <summary>
    /// All fields in declaration order. An owned type declares at least one field: one
    /// without <c>Fields</c>, or with an empty list, is refused with
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>, with the owned type named.
    /// </summary>
    public IReadOnlyList<LatticeField> Fields { get; }

    /// <summary>Finds a field by name.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="field">The field, when found.</param>
    /// <returns><see langword="true"/> when the owned type has the field.</returns>
    public bool TryGetField(string name, [NotNullWhen(true)] out LatticeField? field) =>
        _fieldsByName.TryGetValue(name, out field);
}
