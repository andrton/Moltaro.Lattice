using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Schema;

/// <summary>
/// An entity type declared in the schema: its name, fields, primary key and secondary
/// indexes, and optionally the .NET class that represents its entities.
/// </summary>
/// <remarks>
/// Two rules of the schema language bound what one entity stores, the same in every process,
/// and a database checks them when it opens. What an entity stores at a fixed width, which is
/// every scalar field at its width, the fields of its owned values at every depth, 16 bytes for
/// each String, Binary and List among them, a null bitmap of one bit for each value that can be
/// null and a header of 32 bytes, fits one block of 65,536 bytes. What one element of a List
/// stores in the same way fits one chunk of 65,504 bytes, an element that holds a String, a
/// Binary or a List with room for 64 bytes of each. A database whose schema breaks one does not
/// open: the open fails with <see cref="LatticeErrorEnum.InvalidSchema"/>, with the entity type
/// named. A schema would need thousands of fixed-width fields to meet either.
/// </remarks>
public sealed class LatticeEntityType
{
    private readonly Dictionary<string, LatticeField> _fieldsByName;
    private readonly Dictionary<string, LatticeIndex> _indexesByName;

    // The fields, which the schema alone holds: Fields shows them read-only, so that no caller
    // changes a compiled schema through a cast (LatticeSchema), and the paths of a read and a write
    // take them as a span (FieldsByOrdinal).
    private readonly LatticeField[] _fields;

    internal LatticeEntityType(int id, string name, string? clrTypeName, LatticeField[] fields, LatticeField key, LatticeIndex[] indexes, LatticeStringEncodingEnum stringEncoding, LatticeFingerprint layoutFingerprint)
    {
        Id = id;
        Name = name;
        ClrTypeName = clrTypeName;
        _fields = fields;
        Fields = Array.AsReadOnly(fields);
        Key = key;
        Indexes = Array.AsReadOnly(indexes);
        StringEncoding = stringEncoding;
        LayoutFingerprint = layoutFingerprint;
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
    /// accessed only by name, through records. A .NET class is named by at most one type of
    /// the schema, see <see cref="LatticeOwnedType.ClrTypeName"/>.
    /// </summary>
    public string? ClrTypeName { get; }

    /// <summary>All fields in declaration order.</summary>
    public IReadOnlyList<LatticeField> Fields { get; }

    // The fields in declaration order, which is the order of their ordinals (LatticeField.Ordinal), for
    // the paths of a read and a write: a span over the array Fields shows, indexed without a call
    // through an interface and enumerated without an enumerator.
    internal ReadOnlySpan<LatticeField> FieldsByOrdinal => _fields;

    /// <summary>The primary key field.</summary>
    public LatticeField Key { get; }

    /// <summary>The declared secondary indexes, in declaration order.</summary>
    public IReadOnlyList<LatticeIndex> Indexes { get; }

    // The encoding of the schema, in which the text of an entity is stored: a write counts a String
    // value against the bound of one value in it (ValueBound).
    internal LatticeStringEncodingEnum StringEncoding { get; }

    /// <summary>
    /// The fingerprint of how one entity of this type is stored: the string encoding of the
    /// schema, which field is the primary key, and everything the entity stores, field by
    /// field.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Of the primary key it covers the identity of the key field. Of every field it covers
    /// the identity, type, nullability, string storage and, for an Enum field, the underlying
    /// type of its enum type. For an Owned field it covers the identity of the owned type and
    /// every field of it in the same way as a field of the entity type, and so on through the
    /// owned values inside it, which ends because an owned type does not contain itself, see
    /// <see cref="LatticeOwnedType"/>. For a List, a Map or a Set it covers the declarations
    /// of the element, the key and the value in the same way: type, nullability, string
    /// storage and the underlying type of an enum; an element, a key or a value that is itself
    /// an owned value, a reference or a collection is covered as a field of that type is. For a
    /// Reference it covers the type of the key of the entity type it refers to, and nothing
    /// else of that entity type. So it changes when a field is added to an owned type that the
    /// entity type uses, when the declaration of an element changes, when the key type of a
    /// referenced entity type changes, and when another field becomes the primary key.
    /// </para>
    /// <para>
    /// It does not change when a comparison, an index, a name or the .NET class changes, or
    /// when another entity type changes in a way that does not alter what this one stores.
    /// The identity of the entity type is not part of it either: layout fingerprints are
    /// compared for one entity type in two versions of a schema, and two entity types with
    /// the same fields and the same key field have the same layout fingerprint. Entities of
    /// two schemas whose entity type has the same layout fingerprint are stored alike; when it
    /// differs, the entities of that type have to be converted.
    /// <see cref="LatticeSchema.Fingerprint"/> covers more.
    /// </para>
    /// </remarks>
    public LatticeFingerprint LayoutFingerprint { get; }

    /// <summary>Finds a field by name.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="field">The field, when found.</param>
    /// <returns><see langword="true"/> when the entity type has the field.</returns>
    public bool TryGetField(string name, [NotNullWhen(true)] out LatticeField? field) =>
        _fieldsByName.TryGetValue(name, out field);

    /// <summary>Returns one field directly on this entity type.</summary>
    /// <param name="name">One field name, compared ordinally and case-sensitively.</param>
    /// <exception cref="ArgumentException">The name contains a dot or is absent from this entity type.</exception>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    public LatticeField GetField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return TryGetField(name, out var field)
            ? field
            : throw new ArgumentException($"Entity type '{Name}' has no field '{name}'.", nameof(name));
    }

    /// <summary>Finds a declared secondary index by name.</summary>
    /// <param name="name">The index name.</param>
    /// <param name="index">The index, when found.</param>
    /// <returns><see langword="true"/> when the entity type declares the index.</returns>
    public bool TryGetIndex(string name, [NotNullWhen(true)] out LatticeIndex? index) =>
        _indexesByName.TryGetValue(name, out index);

    /// <summary>Returns the entity type name.</summary>
    public override string ToString() => Name;
}
