using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Schema;

/// <summary>
/// A compiled schema: the validated entity types and enum types of one database with
/// everything resolved that the database needs to serve them: identities, names, fields,
/// primary keys, indexes, the string encoding and the optional .NET classes.
/// </summary>
/// <remarks>
/// Compile a JSON descriptor once with <see cref="Compile"/>, then either use the result
/// directly or <see cref="Save(Stream)"/> it and <see cref="Load(Stream)"/> it at startup.
/// A schema is immutable and can be shared by any number of databases and threads.
/// </remarks>
public sealed class LatticeSchema
{
    private readonly Dictionary<string, LatticeEntityType> _byName;
    private readonly Dictionary<string, LatticeEntityType> _byClrTypeName;

    internal LatticeSchema(int version, IReadOnlyList<LatticeEntityType> entityTypes)
    {
        Version = version;
        EntityTypes = entityTypes;
        _byName = entityTypes.ToDictionary(entityType => entityType.Name, StringComparer.Ordinal);
        _byClrTypeName = entityTypes
            .Where(entityType => entityType.ClrTypeName is not null)
            .ToDictionary(entityType => entityType.ClrTypeName!, StringComparer.Ordinal);
    }

    /// <summary>
    /// The catalog version: the version number declared by the descriptor. It is not part of
    /// <see cref="Fingerprint"/>.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// How the text of the database is encoded in storage: one encoding for every field.
    /// <see cref="LatticeStringEncodingEnum.Utf8"/> when the descriptor does not name one.
    /// </summary>
    public LatticeStringEncodingEnum StringEncoding =>
        throw new NotSupportedException("The string encoding of a schema is not implemented yet.");

    /// <summary>All entity types in declaration order.</summary>
    public IReadOnlyList<LatticeEntityType> EntityTypes { get; }

    /// <summary>All enum types in declaration order; empty when the descriptor declares none.</summary>
    public IReadOnlyList<LatticeEnumType> EnumTypes =>
        throw new NotSupportedException("Enum types are not implemented yet.");

    /// <summary>
    /// The fingerprint of everything in the schema that decides how data is laid out,
    /// compared and constrained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It covers the string encoding; for every enum type its identity, underlying type,
    /// Flags mark and the set of its values; for every entity type its identity and its
    /// primary key, and for every field the identity, type, nullability, comparison, string
    /// storage and enum type; for every index its identity, its fields in order and whether
    /// it is unique.
    /// </para>
    /// <para>
    /// Names, the .NET classes, the catalog version and the order in which the descriptor
    /// lists things are not part of it: a rename does not change it. Two schemas with the
    /// same fingerprint agree on everything that decides how the data of a database is
    /// stored, compared and kept unique. <see cref="LatticeEntityType.LayoutFingerprint"/>
    /// covers only how the entities of one type are stored.
    /// </para>
    /// </remarks>
    public LatticeFingerprint Fingerprint =>
        throw new NotSupportedException("The fingerprint of a schema is not implemented yet.");

    /// <summary>Finds an entity type by name.</summary>
    /// <param name="name">The entity type name.</param>
    /// <param name="entityType">The entity type, when found.</param>
    /// <returns><see langword="true"/> when the schema declares the entity type.</returns>
    public bool TryGetEntityType(string name, [NotNullWhen(true)] out LatticeEntityType? entityType) =>
        _byName.TryGetValue(name, out entityType);

    /// <summary>Finds the entity type that names the given .NET class.</summary>
    /// <param name="clrType">The .NET class.</param>
    /// <param name="entityType">The entity type, when found.</param>
    /// <returns><see langword="true"/> when an entity type declares the class.</returns>
    public bool TryGetEntityType(Type clrType, [NotNullWhen(true)] out LatticeEntityType? entityType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        entityType = null;
        return clrType.FullName is { } fullName && _byClrTypeName.TryGetValue(fullName, out entityType);
    }

    /// <summary>Finds an enum type by name.</summary>
    /// <param name="name">The enum type name.</param>
    /// <param name="enumType">The enum type, when found.</param>
    /// <returns><see langword="true"/> when the schema declares the enum type.</returns>
    public bool TryGetEnumType(string name, [NotNullWhen(true)] out LatticeEnumType? enumType) =>
        throw new NotSupportedException("Enum types are not implemented yet.");

    /// <summary>
    /// Compiles a JSON schema descriptor: parses it, validates it and resolves its entity
    /// types, fields and primary keys.
    /// </summary>
    /// <param name="json">The JSON descriptor.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>: the descriptor is not valid.
    /// </exception>
    public static LatticeSchema Compile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return SchemaJsonReader.Read(json);
    }

    /// <summary>Loads a compiled schema written by <see cref="Save(Stream)"/>.</summary>
    /// <param name="stream">The stream to read from. It stays open.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>: the data is not a compiled schema of a
    /// supported format.
    /// </exception>
    public static LatticeSchema Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return SchemaBinaryFormat.Read(stream);
    }

    /// <summary>Loads a compiled schema from a file written by <see cref="Save(string)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>: the file is not a compiled schema of a
    /// supported format.
    /// </exception>
    public static LatticeSchema Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>Writes the compiled schema in its binary form.</summary>
    /// <param name="stream">The stream to write to. It stays open.</param>
    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        SchemaBinaryFormat.Write(this, stream);
    }

    /// <summary>Writes the compiled schema to a file, replacing an existing one.</summary>
    /// <param name="path">The file path.</param>
    public void Save(string path)
    {
        using var stream = File.Create(path);
        Save(stream);
    }
}
