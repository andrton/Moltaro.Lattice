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
    /// a Set is what a Map key may be, and two members are equal as two keys are, see
    /// <see cref="Key"/>; a nullable element allows one <see langword="null"/> member.
    /// <see langword="null"/> when the type is neither
    /// <see cref="LatticeFieldTypeEnum.List"/> nor <see cref="LatticeFieldTypeEnum.Set"/>.
    /// </summary>
    public LatticeValueDeclaration? Element =>
        throw new NotSupportedException("Collections are not implemented yet.");

    /// <summary>
    /// The declaration of the keys of a Map, never nullable. A Map key, like a Set element, is
    /// one of three: a scalar that is not <see cref="LatticeFieldTypeEnum.Binary"/>, an
    /// <see cref="LatticeFieldTypeEnum.Enum"/>, a <see cref="LatticeFieldTypeEnum.Reference"/>.
    /// Two keys are equal as this declaration compares them: a String by its
    /// <see cref="Comparison"/>, and a Reference as the key of its target, with the comparison
    /// of that key. <see langword="null"/> when the type is not
    /// <see cref="LatticeFieldTypeEnum.Map"/>.
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
    /// The .NET type of a non-null value at this position: what a record holds for it, at
    /// every depth, and for a scalar and an Enum also what a record accepts. The remarks say
    /// what a record accepts for the other types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For an <see cref="LatticeFieldTypeEnum.Enum"/> it is the .NET type of the underlying
    /// integer: a record holds that integer.
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
    /// of it when that position is nullable and its type is a value type. That is what a
    /// record holds at every depth: an owned value inside a List or a Map of a record is a
    /// <c>LatticeOwnedValue</c> too, whether or not its owned type names a class. The
    /// documentation of <c>LatticeRecord</c> says where a record takes these types exactly and
    /// what it keeps of what it is given.
    /// </para>
    /// <para>
    /// This property says nothing about a class. A bound class holds the same data in its own
    /// types, by the rule that <see cref="ILatticeDatabase.Set{TEntity}"/> states.
    /// </para>
    /// </remarks>
    public Type ValueType { get; }
}
