using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>Specifies the form of one field in a generated view when the build has no schema file.</summary>
/// <remarks>Use separate attributes for different field paths.</remarks>
[AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class LatticeViewFieldAttribute : Attribute
{
    /// <summary>Specifies the nullability of a borrowed field.</summary>
    /// <param name="path">The case-sensitive path to the field. A dot separates owned fields.</param>
    public LatticeViewFieldAttribute(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        Path = path;
    }

    /// <summary>Chooses a field kind when the CLR property type can represent more than one.</summary>
    /// <param name="path">The case-sensitive path to the field. A dot separates owned fields.</param>
    /// <param name="kind">The field kind to use in the generated view.</param>
    public LatticeViewFieldAttribute(string path, LatticeFieldTypeEnum kind)
        : this(path)
    {
        Kind = kind;
    }

    /// <summary>The case-sensitive field path within the entity type.</summary>
    public string Path { get; }

    /// <summary>The chosen field kind, when specified.</summary>
    public LatticeFieldTypeEnum? Kind { get; }

    /// <summary>
    /// Whether the borrowed field can be absent. The generator uses this value only when the named
    /// argument is written on the attribute.
    /// </summary>
    public bool IsNullable { get; set; }

    /// <summary>Omits a CLR-only field path from a projection generated without a schema file.</summary>
    /// <remarks>
    /// The actual schema must also omit that path. When a schema file is supplied to the
    /// build, applying this option to a field in that file is a build error.
    /// </remarks>
    public bool Ignore { get; set; }
}
