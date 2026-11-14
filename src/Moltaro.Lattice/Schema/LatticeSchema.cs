using System.Diagnostics.CodeAnalysis;

namespace Moltaro.Lattice.Schema;

/// <summary>
/// A compiled schema: the validated entity types of one database with everything resolved
/// that the database needs to serve them: names, fields, primary keys and the optional
/// .NET classes.
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

    /// <summary>The version number declared by the descriptor.</summary>
    public int Version { get; }

    /// <summary>All entity types in declaration order.</summary>
    public IReadOnlyList<LatticeEntityType> EntityTypes { get; }

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
