using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Schema;

/// <summary>
/// A compiled schema: the validated entity types, owned types and enum types of one database
/// with everything resolved that the database needs to serve them: identities, names, fields,
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
    private readonly Dictionary<string, LatticeEnumType> _enumTypesByName;

    internal LatticeSchema(
        int version,
        LatticeStringEncodingEnum stringEncoding,
        IReadOnlyList<LatticeEnumType> enumTypes,
        IReadOnlyList<LatticeEntityType> entityTypes,
        LatticeFingerprint fingerprint)
    {
        Version = version;
        StringEncoding = stringEncoding;
        EnumTypes = enumTypes;
        EntityTypes = entityTypes;
        Fingerprint = fingerprint;
        _byName = entityTypes.ToDictionary(entityType => entityType.Name, StringComparer.Ordinal);
        _byClrTypeName = entityTypes
            .Where(entityType => entityType.ClrTypeName is not null)
            .ToDictionary(entityType => entityType.ClrTypeName!, StringComparer.Ordinal);
        _enumTypesByName = enumTypes.ToDictionary(enumType => enumType.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// The catalog version: a whole number from 1, declared by the descriptor as
    /// <c>SchemaVersion</c>, or 1 when the descriptor does not declare it. A later version of a
    /// schema has a greater number. It is not part of <see cref="Fingerprint"/>.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// How the text of the database is encoded in storage: one encoding for every field.
    /// <see cref="LatticeStringEncodingEnum.Utf8"/> when the descriptor does not name one.
    /// </summary>
    public LatticeStringEncodingEnum StringEncoding { get; }

    /// <summary>All entity types in declaration order.</summary>
    public IReadOnlyList<LatticeEntityType> EntityTypes { get; }

    /// <summary>All enum types in declaration order; empty when the descriptor declares none.</summary>
    public IReadOnlyList<LatticeEnumType> EnumTypes { get; }

    /// <summary>All owned types in declaration order; empty when the descriptor declares none.</summary>
    public IReadOnlyList<LatticeOwnedType> OwnedTypes =>
        throw new NotSupportedException("Owned types are not implemented yet.");

    /// <summary>
    /// The fingerprint of everything in the schema that decides how data is laid out,
    /// compared and constrained.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It covers the string encoding; for every enum type its identity, underlying type,
    /// Flags mark and the set of its values; for every owned type its identity and its
    /// fields; for every entity type its identity, its primary key, its fields and its
    /// indexes. Of a field it covers the identity, type, nullability, comparison, string
    /// storage and enum type, and in the same way the owned type of an owned value, the
    /// entity type a reference refers to, and the declarations of the element, the key and
    /// the value of a collection. Of an index it covers the identity, whether it is unique,
    /// and its parts in order, each with its path from a field of the entity type to the
    /// indexed field.
    /// </para>
    /// <para>
    /// Names, the .NET classes, the catalog version and the order in which the descriptor
    /// lists things are not part of it: a rename does not change it. Two schemas with the
    /// same fingerprint agree on everything that decides how the data of a database is
    /// stored, compared and kept unique. <see cref="LatticeEntityType.LayoutFingerprint"/>
    /// covers only how the entities of one type are stored.
    /// </para>
    /// </remarks>
    public LatticeFingerprint Fingerprint { get; }

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
        _enumTypesByName.TryGetValue(name, out enumType);

    /// <summary>Finds an owned type by name.</summary>
    /// <param name="name">The owned type name.</param>
    /// <param name="ownedType">The owned type, when found.</param>
    /// <returns><see langword="true"/> when the schema declares the owned type.</returns>
    public bool TryGetOwnedType(string name, [NotNullWhen(true)] out LatticeOwnedType? ownedType) =>
        throw new NotSupportedException("Owned types are not implemented yet.");

    /// <summary>
    /// Compiles a JSON schema descriptor: parses it, validates it and resolves its enum
    /// types, owned types, entity types, fields, primary keys and indexes.
    /// </summary>
    /// <param name="json">The JSON descriptor.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>: the descriptor is not valid. That
    /// includes text that is not well-formed: a surrogate without its pair, whether written
    /// as it is or escaped, such as <c>\ud800</c>, is refused and never replaced.
    /// </exception>
    public static LatticeSchema Compile(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return SchemaJsonReader.Read(json);
    }

    /// <summary>
    /// Loads a compiled schema written by <see cref="Save(Stream)"/>. It reads one schema from
    /// the current position and leaves the stream right after it: what follows belongs to the
    /// caller.
    /// </summary>
    /// <param name="stream">The stream to read from. It stays open.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>: the data is not a compiled schema of a
    /// supported format, it is truncated or damaged, or the schema it holds breaks a rule
    /// that <see cref="Compile"/> checks in a descriptor. Text in it that is not well-formed
    /// UTF-8 is damage: it is refused and never replaced by other text. No damage ends in
    /// another exception; only a failure of the stream itself is reported as the stream
    /// reports it. The compiled form ends with a digest of itself, SHA-256. A change of any
    /// byte is damage and is refused, also when the changed bytes would still read as a
    /// schema; nothing is decoded from data whose digest does not agree. The digest detects
    /// damage. It is not a signature: it does not show who wrote the data.
    /// </exception>
    public static LatticeSchema Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return SchemaBinaryFormat.Read(stream);
    }

    /// <summary>
    /// Loads a compiled schema from a file written by <see cref="Save(string)"/>. The file
    /// holds one compiled schema and nothing after it.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <exception cref="LatticeException">
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>: the file is not a compiled schema of a
    /// supported format, it is truncated or damaged, as for <see cref="Load(Stream)"/>, or it
    /// has data after the schema. Only a failure to open or read the file is reported as the
    /// file system reports it.
    /// </exception>
    public static LatticeSchema Load(string path) => SchemaBinaryFormat.ReadFile(path);

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
