namespace Moltaro.Lattice.Schema;

/// <summary>
/// Type of a field declared in the schema. The names are the ones used in the schema descriptor.
/// </summary>
public enum LatticeFieldTypeEnum : byte
{
    /// <summary><see cref="bool"/>.</summary>
    Boolean = 1,

    /// <summary><see cref="sbyte"/>.</summary>
    Int8 = 2,

    /// <summary><see cref="byte"/>.</summary>
    UInt8 = 3,

    /// <summary><see cref="short"/>.</summary>
    Int16 = 4,

    /// <summary><see cref="ushort"/>.</summary>
    UInt16 = 5,

    /// <summary><see cref="int"/>.</summary>
    Int32 = 6,

    /// <summary><see cref="uint"/>.</summary>
    UInt32 = 7,

    /// <summary><see cref="long"/>.</summary>
    Int64 = 8,

    /// <summary><see cref="ulong"/>.</summary>
    UInt64 = 9,

    /// <summary><see cref="float"/>.</summary>
    Float32 = 10,

    /// <summary><see cref="double"/>.</summary>
    Float64 = 11,

    /// <summary><see cref="decimal"/>.</summary>
    Decimal = 12,

    /// <summary><see cref="string"/>.</summary>
    String = 13,

    /// <summary><see cref="System.Guid"/>.</summary>
    Guid = 14,

    /// <summary><see cref="System.DateTimeOffset"/>.</summary>
    DateTimeOffset = 15,

    /// <summary><see cref="System.DateOnly"/>.</summary>
    DateOnly = 16,

    /// <summary><see cref="System.TimeOnly"/>.</summary>
    TimeOnly = 17,

    /// <summary><see cref="System.TimeSpan"/>.</summary>
    TimeSpan = 18,

    /// <summary>An array of <see cref="byte"/>.</summary>
    Binary = 19,

    /// <summary>
    /// A value of an enum type declared in the schema, see
    /// <see cref="LatticeValueDeclaration.EnumType"/>. It is stored as its underlying integer,
    /// and its .NET type is that integer; a bound class may declare the property as a .NET
    /// enum that agrees with the enum type, as <see cref="LatticeValueDeclaration.ValueType"/>
    /// states.
    /// </summary>
    /// <remarks>
    /// <b>Not implemented yet.</b> A schema declares Enum fields, and is compiled, saved and
    /// loaded with them, but storing their values is not implemented: every write of an
    /// entity type that has an Enum field throws <see cref="NotSupportedException"/>, whatever
    /// the value.
    /// </remarks>
    Enum = 20,
}
