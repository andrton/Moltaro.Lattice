namespace Moltaro.Lattice.Schema;

/// <summary>
/// The declaration of a value: its type, whether it may be <see langword="null"/>, and the
/// policies that come with its type. Every field has one, see
/// <see cref="LatticeField.Declaration"/>.
/// </summary>
/// <remarks>
/// A policy that does not apply to the type is <see langword="null"/>: a comparison and a
/// string storage only for <see cref="LatticeFieldTypeEnum.String"/>, an enum type only for
/// <see cref="LatticeFieldTypeEnum.Enum"/>. A policy that the descriptor does not state has
/// its default: not nullable, <see cref="LatticeStringComparisonEnum.Ordinal"/>,
/// <see cref="LatticeStringStorageEnum.Plain"/>.
/// </remarks>
public sealed class LatticeValueDeclaration
{
    internal LatticeValueDeclaration(LatticeFieldTypeEnum type, bool isNullable, LatticeStringComparisonEnum? comparison, LatticeStringStorageEnum? stringStorage, LatticeEnumType? enumType)
    {
        Type = type;
        IsNullable = isNullable;
        Comparison = comparison;
        StringStorage = stringStorage;
        EnumType = enumType;
        ValueType = FieldTypes.ValueType(enumType?.UnderlyingType ?? type);
    }

    /// <summary>The declared type.</summary>
    public LatticeFieldTypeEnum Type { get; }

    /// <summary>
    /// Whether the value may be <see langword="null"/>. Zero, <see langword="false"/> and an
    /// empty string are values, distinct from <see langword="null"/>.
    /// </summary>
    public bool IsNullable { get; }

    /// <summary>
    /// How text is compared for equality and order, see
    /// <see cref="LatticeStringComparisonEnum"/>. <see langword="null"/> when the type is not
    /// <see cref="LatticeFieldTypeEnum.String"/>.
    /// </summary>
    public LatticeStringComparisonEnum? Comparison { get; }

    /// <summary>
    /// How text is stored, see <see cref="LatticeStringStorageEnum"/>.
    /// <see langword="null"/> when the type is not <see cref="LatticeFieldTypeEnum.String"/>.
    /// </summary>
    public LatticeStringStorageEnum? StringStorage { get; }

    /// <summary>
    /// The enum type whose values are held. <see langword="null"/> when the type is not
    /// <see cref="LatticeFieldTypeEnum.Enum"/>.
    /// </summary>
    public LatticeEnumType? EnumType { get; }

    /// <summary>
    /// The .NET type of a non-null value: what a record holds and accepts for it, and the
    /// type of the property of a bound class, as <see cref="Nullable{T}"/> of it when the
    /// value is nullable and the type is a value type.
    /// </summary>
    /// <remarks>
    /// For an <see cref="LatticeFieldTypeEnum.Enum"/> it is the .NET type of the underlying
    /// integer: a record holds that integer. A bound class may declare the property as a .NET
    /// enum with the same underlying type instead. A .NET enum that has two names for one
    /// value binds only when the schema declares that value with a canonical name.
    /// </remarks>
    public Type ValueType { get; }
}
