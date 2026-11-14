namespace Moltaro.Lattice.Schema;

/// <summary>
/// The declaration of the value at one position: its type, whether it may be
/// <see langword="null"/>, the policies that come with its type and, for an owned value, a
/// reference or a collection, what it holds. Every field has one, see
/// <see cref="LatticeField.Declaration"/>, and so do the element, the key and the value of a
/// collection.
/// </summary>
/// <remarks>
/// A policy that does not apply to the type is <see langword="null"/>: a comparison and a
/// string storage only for <see cref="LatticeFieldTypeEnum.String"/>, an enum type only for
/// <see cref="LatticeFieldTypeEnum.Enum"/>. A policy that the descriptor does not state has
/// its default: not nullable, <see cref="LatticeStringComparisonEnum.Ordinal"/>,
/// <see cref="LatticeStringStorageEnum.Plain"/>. In the same way the owned type is there only
/// for <see cref="LatticeFieldTypeEnum.Owned"/>, the referenced entity type only for
/// <see cref="LatticeFieldTypeEnum.Reference"/>, the element only for
/// <see cref="LatticeFieldTypeEnum.List"/> and <see cref="LatticeFieldTypeEnum.Set"/>, and
/// the key and the value only for <see cref="LatticeFieldTypeEnum.Map"/>.
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
    /// Whether the value may be <see langword="null"/>. Zero, <see langword="false"/>, an
    /// empty string and an empty collection are values, distinct from <see langword="null"/>.
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
    /// The owned type whose value is held. <see langword="null"/> when the type is not
    /// <see cref="LatticeFieldTypeEnum.Owned"/>.
    /// </summary>
    public LatticeOwnedType? OwnedType =>
        throw new NotSupportedException("Owned types are not implemented yet.");

    /// <summary>
    /// The name of the entity type that a reference refers to: the value is the primary key of
    /// an entity of that type. The schema declares that entity type, see
    /// <see cref="LatticeSchema.TryGetEntityType(string, out LatticeEntityType)"/>.
    /// <see langword="null"/> when the type is not <see cref="LatticeFieldTypeEnum.Reference"/>.
    /// </summary>
    public string? ReferencedEntityTypeName =>
        throw new NotSupportedException("References are not implemented yet.");

    /// <summary>
    /// The declaration of the elements of a List or a Set. An element of a List may be
    /// anything a field may be, an owned value and another collection included. An element of
    /// a Set is a scalar other than <see cref="LatticeFieldTypeEnum.Binary"/>, an Enum or a
    /// Reference, and two members are equal as this declaration compares them; a nullable
    /// element allows one <see langword="null"/> member. <see langword="null"/> when the type
    /// is neither <see cref="LatticeFieldTypeEnum.List"/> nor
    /// <see cref="LatticeFieldTypeEnum.Set"/>.
    /// </summary>
    public LatticeValueDeclaration? Element =>
        throw new NotSupportedException("Collections are not implemented yet.");

    /// <summary>
    /// The declaration of the keys of a Map: a scalar other than
    /// <see cref="LatticeFieldTypeEnum.Binary"/>, an Enum or a Reference, never nullable. Two
    /// keys are equal as this declaration compares them. <see langword="null"/> when the type
    /// is not <see cref="LatticeFieldTypeEnum.Map"/>.
    /// </summary>
    public LatticeValueDeclaration? Key =>
        throw new NotSupportedException("Collections are not implemented yet.");

    /// <summary>
    /// The declaration of the values of a Map: anything a field may be, an owned value and
    /// another collection included. <see langword="null"/> when the type is not
    /// <see cref="LatticeFieldTypeEnum.Map"/>.
    /// </summary>
    public LatticeValueDeclaration? Value =>
        throw new NotSupportedException("Collections are not implemented yet.");

    /// <summary>
    /// The .NET type of a non-null value at this position: what a record holds for it. For a
    /// scalar, an Enum and a Reference it is also what a record accepts and the type of the
    /// property of a bound class, as <see cref="Nullable{T}"/> of it when the value is
    /// nullable and the type is a value type. For an owned value and a collection the remarks
    /// say what a record accepts and what a bound class declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For an <see cref="LatticeFieldTypeEnum.Enum"/> it is the .NET type of the underlying
    /// integer: a record holds that integer. A bound class may declare the property as that
    /// integer too; it then holds the value as it is stored and is not compared with the
    /// members of the enum type.
    /// </para>
    /// <para>
    /// A bound class may instead declare the property as a .NET enum, as
    /// <see cref="Nullable{T}"/> of it when the value is nullable. That enum has to agree with
    /// the enum type of the field: the same underlying type, the same members, every name
    /// with its value in both directions, and <see cref="FlagsAttribute"/> exactly when the
    /// enum type is a Flags type. A .NET enum with two names for one value therefore agrees
    /// only with an enum type that declares both names. Nothing is mapped or guessed: a class
    /// whose enum does not agree is refused with <see cref="LatticeErrorEnum.BindingFailed"/>,
    /// with the property named, when the class is bound, and not later when a value is read.
    /// </para>
    /// <para>What a record holds at a position, which is what this property reports for it:</para>
    /// <list type="table">
    /// <listheader>
    /// <term>Declared</term>
    /// <description>In a record; accepted when set</description>
    /// </listheader>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Owned"/></term>
    /// <description>
    /// <c>LatticeOwnedValue</c>; accepted: a <c>LatticeOwnedValue</c> of that owned type.
    /// </description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Reference"/></term>
    /// <description>
    /// The key of the target, in the .NET type of that key; accepted: the same.
    /// </description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.List"/> of T</term>
    /// <description>
    /// <c>List&lt;T&gt;</c>; accepted: any <c>IEnumerable&lt;T&gt;</c>, copied.
    /// </description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Map"/> of K, V</term>
    /// <description>
    /// <c>Dictionary&lt;K, V&gt;</c> with the comparer of the key declaration; accepted: any
    /// <c>IEnumerable&lt;KeyValuePair&lt;K, V&gt;&gt;</c>, copied. A key given twice is an
    /// <see cref="ArgumentException"/>.
    /// </description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.Set"/> of T</term>
    /// <description>
    /// <c>HashSet&lt;T&gt;</c> with the comparer of the element declaration; accepted: any
    /// <c>IEnumerable&lt;T&gt;</c>, copied. Of equal members the first is kept.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// T is the <see cref="ValueType"/> of the inner declaration, or <see cref="Nullable{T}"/>
    /// of it when that position is nullable and its type is a value type.
    /// </para>
    /// <para>
    /// In a bound class a List is a property of <c>List&lt;T&gt;</c> or <c>T[]</c>, a Map of
    /// <c>Dictionary&lt;K, V&gt;</c>, a Set of <c>HashSet&lt;T&gt;</c>, an owned value of the
    /// class named by <see cref="LatticeOwnedType.ClrTypeName"/> of its owned type, and a
    /// reference of the key type of its target. A property of another type is refused with
    /// <see cref="LatticeErrorEnum.BindingFailed"/>, with the property named.
    /// </para>
    /// </remarks>
    public Type ValueType { get; }
}
