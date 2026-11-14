using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Schema;

/// <summary>
/// An entity type declared in the schema: its name, fields, primary key and secondary
/// indexes, and optionally the .NET class that represents its entities.
/// </summary>
public sealed class LatticeEntityType
{
    private readonly Dictionary<string, LatticeField> _fieldsByName;
    private readonly Dictionary<string, LatticeIndex> _indexesByName;

    internal LatticeEntityType(int id, string name, string? clrTypeName, IReadOnlyList<LatticeField> fields, LatticeField key, IReadOnlyList<LatticeIndex> indexes)
    {
        Id = id;
        Name = name;
        ClrTypeName = clrTypeName;
        Fields = fields;
        Key = key;
        Indexes = indexes;
        _fieldsByName = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        _indexesByName = indexes.ToDictionary(index => index.Name, StringComparer.Ordinal);
    }

    /// <summary>The stable identity of the entity type within the schema.</summary>
    public int Id { get; }

    /// <summary>The entity type name. Every entity type is accessible by it.</summary>
    public string Name { get; }

    /// <summary>
    /// The full name of the .NET class that represents entities of this type, such as
    /// <c>MyApp.Directory.User</c>. Optional: <see langword="null"/> when the entity type is
    /// accessed only by name, through records.
    /// </summary>
    public string? ClrTypeName { get; }

    /// <summary>All fields in declaration order.</summary>
    public IReadOnlyList<LatticeField> Fields { get; }

    /// <summary>The primary key field.</summary>
    public LatticeField Key { get; }

    /// <summary>The declared secondary indexes, in declaration order.</summary>
    public IReadOnlyList<LatticeIndex> Indexes { get; }

    /// <summary>
    /// The fingerprint of how one entity of this type is stored: the string encoding of the
    /// schema and, for every field, its identity, type, nullability, string storage and, for
    /// an Enum field, the underlying type of its enum type.
    /// </summary>
    /// <remarks>
    /// It does not change when a comparison, an index, a name or the .NET class changes, or
    /// when another entity type changes in a way that does not alter what this one stores.
    /// Entities of two schemas whose entity type has the same layout fingerprint are stored
    /// alike; when it differs, the entities of that type have to be converted.
    /// <see cref="LatticeSchema.Fingerprint"/> covers more.
    /// </remarks>
    public LatticeFingerprint LayoutFingerprint =>
        throw new NotSupportedException("The layout fingerprint of an entity type is not implemented yet.");

    /// <summary>Finds a field by name.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="field">The field, when found.</param>
    /// <returns><see langword="true"/> when the entity type has the field.</returns>
    public bool TryGetField(string name, [NotNullWhen(true)] out LatticeField? field) =>
        _fieldsByName.TryGetValue(name, out field);

    /// <summary>Finds a declared secondary index by name.</summary>
    /// <param name="name">The index name.</param>
    /// <param name="index">The index, when found.</param>
    /// <returns><see langword="true"/> when the entity type declares the index.</returns>
    public bool TryGetIndex(string name, [NotNullWhen(true)] out LatticeIndex? index) =>
        _indexesByName.TryGetValue(name, out index);

    /// <summary>Returns the entity type name.</summary>
    public override string ToString() => Name;
}
