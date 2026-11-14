namespace Moltaro.Lattice.Schema;

/// <summary>
/// An enum type declared in the schema: named integer values that a field of type
/// <see cref="LatticeFieldTypeEnum.Enum"/> holds. An ordinary enum type holds one declared
/// value; a Flags enum type holds a combination of declared bits.
/// </summary>
/// <remarks>
/// <para>
/// The schema is the authority for an enum type, not a .NET enum. An entity stores the
/// integer value; the names are metadata of the schema and are not stored with it. Only a
/// valid value is stored, see <see cref="IsValid"/>: a write with another value fails
/// validation, and the value is never changed into a valid one.
/// </para>
/// <para>
/// Values compare and sort as numbers of the underlying type, with its sign: neither the
/// names nor the order of declaration decide the order. A Flags value compares as its whole
/// mask.
/// </para>
/// </remarks>
public sealed class LatticeEnumType
{
    internal LatticeEnumType(int id, string name, LatticeFieldTypeEnum underlyingType, bool isFlags, IReadOnlyList<LatticeEnumMember> members)
    {
        Id = id;
        Name = name;
        UnderlyingType = underlyingType;
        IsFlags = isFlags;
        Members = members;
    }

    /// <summary>The stable identity of the enum type within the schema.</summary>
    public int Id { get; }

    /// <summary>The enum type name, unique within the schema. A field of the enum type names it.</summary>
    public string Name { get; }

    /// <summary>
    /// The integer type a value is stored as: one of the eight integer types, from
    /// <see cref="LatticeFieldTypeEnum.Int8"/> to <see cref="LatticeFieldTypeEnum.UInt64"/>.
    /// <see cref="LatticeFieldTypeEnum.Int32"/> when the descriptor does not name one.
    /// </summary>
    public LatticeFieldTypeEnum UnderlyingType { get; }

    /// <summary>
    /// Whether the enum type is a set of flags: a value is then any combination of the
    /// declared bits, zero included, instead of one declared value. In a Flags enum type
    /// every member that names an independent flag has exactly one bit, and a member that
    /// names a combination contains only declared bits.
    /// </summary>
    public bool IsFlags { get; }

    /// <summary>The declared members, in declaration order.</summary>
    public IReadOnlyList<LatticeEnumMember> Members { get; }

    /// <summary>
    /// Whether a value may be stored in a field of this enum type. For an ordinary enum type
    /// it is a declared value. For a Flags enum type it is zero, the empty set, or any
    /// combination of declared bits; a value with a bit that no member declares is not
    /// valid, and no bit is removed to make it valid. A value outside the range of the
    /// underlying type is never valid.
    /// </summary>
    /// <param name="value">The value, as an integer of any width.</param>
    /// <returns><see langword="true"/> when the value is valid.</returns>
    public bool IsValid(Int128 value) =>
        throw new NotSupportedException("Validating an enum value is not implemented yet.");
}
