namespace Moltaro.Lattice.Schema;

/// <summary>
/// The declaration of the value at one position: its type, whether it may be
/// <see langword="null"/>, the policies that come with its type and, for an owned value, a
/// reference or a collection, what it holds. Every field has one, see
/// <see cref="LatticeField.Declaration"/>, and so do the element, the key and the value of a
/// collection.
/// </summary>
/// <remarks>
/// <para>
/// A policy that does not apply to the type is <see langword="null"/>: a comparison and a
/// string storage only for <see cref="LatticeFieldTypeEnum.String"/>, an enum type only for
/// <see cref="LatticeFieldTypeEnum.Enum"/>. A policy that the descriptor does not state has
/// its default: not nullable, <see cref="LatticeStringComparisonEnum.Ordinal"/>,
/// <see cref="LatticeStringStorageEnum.Plain"/>. In the same way the owned type is there only
/// for <see cref="LatticeFieldTypeEnum.Owned"/>, the referenced entity type only for
/// <see cref="LatticeFieldTypeEnum.Reference"/>, the element only for
/// <see cref="LatticeFieldTypeEnum.List"/> and <see cref="LatticeFieldTypeEnum.Set"/>, and
/// the key and the value only for <see cref="LatticeFieldTypeEnum.Map"/>.
/// </para>
/// <para>
/// <b>A declaration is at most 32 deep</b>, counted through collections and owned types
/// together. The depth of a declaration is 1 for a scalar, a Binary, an Enum and a Reference;
/// for a List and a Set one more than the depth of its element; for a Map one more than the
/// deeper of its key and its value; for an Owned one more than the deepest field of its owned
/// type. The declaration of every field, of an entity type and of an owned type, is 32 deep
/// or less: a deeper one is refused with <see cref="LatticeErrorEnum.InvalidSchema"/>, with
/// the field named, in a saved schema and in a descriptor. It is a fixed rule of the schema
/// language, the same in every process, and not an option. The text of a descriptor is read
/// to a depth of 128, the root object counting as 1, four times the depth of a declaration
/// and far deeper than any descriptor of a schema can be: text nested deeper is refused as a
/// whole with <see cref="LatticeErrorEnum.InvalidSchema"/>, without a field named.
/// </para>
/// </remarks>
public sealed class LatticeValueDeclaration
{
    // SchemaBuilder gives each type what it has and nothing else: the policies to a String,
    // the enum type to an Enum, the owned type to an Owned value, the target to a Reference,
    // the element to a List and a Set, the key and the value to a Map.
    internal LatticeValueDeclaration(
        LatticeFieldTypeEnum type,
        bool isNullable,
        LatticeStringComparisonEnum? comparison = null,
        LatticeStringStorageEnum? stringStorage = null,
        LatticeEnumType? enumType = null,
        LatticeOwnedType? ownedType = null,
        ReferenceTarget? target = null,
        LatticeValueDeclaration? element = null,
        LatticeValueDeclaration? key = null,
        LatticeValueDeclaration? value = null)
    {
        Type = type;
        IsNullable = isNullable;
        Comparison = comparison;
        StringStorage = stringStorage;
        EnumType = enumType;
        OwnedType = ownedType;
        Target = target;
        Element = element;
        Key = key;
        Value = value;

        ScalarType = enumType?.UnderlyingType ?? target?.Key.Type ?? type;
        TextComparison = type == LatticeFieldTypeEnum.String
            ? (comparison == LatticeStringComparisonEnum.OrdinalIgnoreCase ? TextComparison.WithoutCase : TextComparison.Ordinal)
            : target?.Key.TextComparison;

        if (type is LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set)
        {
            Sequence = SequenceForm.For(type == LatticeFieldTypeEnum.Set, element!);
        }
        else if (type == LatticeFieldTypeEnum.Map)
        {
            Map = MapForm.For(key!, value!);
        }

        // Every part a declaration holds is built before it, so its depth is taken from theirs
        // and nothing is walked here.
        Depth = type switch
        {
            LatticeFieldTypeEnum.Owned => 1 + ownedType!.Depth,
            LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set => 1 + element!.Depth,
            LatticeFieldTypeEnum.Map => 1 + Math.Max(key!.Depth, value!.Depth),
            _ => 1,
        };

        // An owned value is held as a LatticeOwnedValue of the access namespace: the public
        // contract names that type as the ValueType of an Owned position, so this is the one
        // place where the schema refers to it. It is exception X5 of docs/work/exceptions.md.
        ValueType = type switch
        {
            LatticeFieldTypeEnum.Owned => typeof(Access.LatticeOwnedValue),
            LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set => Sequence!.ValueType,
            LatticeFieldTypeEnum.Map => Map!.ValueType,
            _ => FieldTypes.ValueType(ScalarType),
        };

        PositionType = isNullable && ValueType.IsValueType ? typeof(Nullable<>).MakeGenericType(ValueType) : ValueType;
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
    public LatticeOwnedType? OwnedType { get; }

    /// <summary>
    /// The name of the entity type that a reference refers to: the value is the primary key of
    /// an entity of that type. The schema declares that entity type, see
    /// <see cref="LatticeSchema.TryGetEntityType(string, out LatticeEntityType)"/>.
    /// <see langword="null"/> when the type is not <see cref="LatticeFieldTypeEnum.Reference"/>.
    /// </summary>
    public string? ReferencedEntityTypeName => Target?.EntityTypeName;

    /// <summary>
    /// The declaration of the elements of a List or a Set. An element of a List may be
    /// anything a field may be, an owned value and another collection included, within the
    /// depth a declaration may have, see the remarks of this class. An element of
    /// a Set is what a Map key may be, and two members are equal as two keys are, see
    /// <see cref="Key"/>; a nullable element allows one <see langword="null"/> member.
    /// <see langword="null"/> when the type is neither
    /// <see cref="LatticeFieldTypeEnum.List"/> nor <see cref="LatticeFieldTypeEnum.Set"/>.
    /// </summary>
    public LatticeValueDeclaration? Element { get; }

    /// <summary>
    /// The declaration of the keys of a Map, never nullable. A Map key, like a Set element, is
    /// one of three: a scalar that is not <see cref="LatticeFieldTypeEnum.Binary"/>, an
    /// <see cref="LatticeFieldTypeEnum.Enum"/>, a <see cref="LatticeFieldTypeEnum.Reference"/>.
    /// Two keys are equal as this declaration compares them: a String by its
    /// <see cref="Comparison"/>, and a Reference as the key of its target, with the comparison
    /// of that key. <see langword="null"/> when the type is not
    /// <see cref="LatticeFieldTypeEnum.Map"/>.
    /// </summary>
    public LatticeValueDeclaration? Key { get; }

    /// <summary>
    /// The declaration of the values of a Map: anything a field may be, an owned value and
    /// another collection included, within the depth a declaration may have, see the remarks
    /// of this class. <see langword="null"/> when the type is not
    /// <see cref="LatticeFieldTypeEnum.Map"/>.
    /// </summary>
    public LatticeValueDeclaration? Value { get; }

    /// <summary>
    /// The .NET type of a non-null value at this position: what a record holds for it, at
    /// every depth. The remarks say what a record accepts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For an <see cref="LatticeFieldTypeEnum.Enum"/> it is the .NET type of the underlying
    /// integer: a record holds that integer.
    /// </para>
    /// <para>
    /// For the value of a field of a record or of an owned value that is a scalar, an Enum or a
    /// Reference, a record accepts a value of this type and a number of another type that is the
    /// same number in it, by the one rule of a part of a search key, see
    /// <see cref="Access.IndexKey"/>, and holds it as this type. At a position inside a
    /// collection it accepts this type only.
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
    /// The key of the target, in the .NET type of that key; accepted: the same, and for the value of a field a number as the paragraph above says.
    /// </description>
    /// </item>
    /// <item>
    /// <term><see cref="LatticeFieldTypeEnum.List"/> of T</term>
    /// <description>
    /// <c>List&lt;T&gt;</c>; accepted: an <c>IEnumerable&lt;T&gt;</c>, copied.
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
    /// <c>HashSet&lt;T&gt;</c> with the comparer of the element declaration; accepted: an
    /// <c>IEnumerable&lt;T&gt;</c>, copied. Of equal members the first is kept.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// T is the <see cref="ValueType"/> of the inner declaration, or <see cref="Nullable{T}"/>
    /// of it when that position is nullable and its type is a value type. That is what a
    /// record holds at every depth: an owned value inside a List or a Map of a record is a
    /// <c>LatticeOwnedValue</c> too, whether or not its owned type names a class. The
    /// documentation of <c>LatticeRecord</c> says which inputs a record takes for these types,
    /// an array that the runtime lets pass for an <c>IEnumerable&lt;T&gt;</c> included, and
    /// what it keeps of what it is given.
    /// </para>
    /// <para>
    /// This property says nothing about a class. A bound class holds the same data in its own
    /// types, by the rule that <see cref="ILatticeDatabase.Set{TEntity}"/> states.
    /// </para>
    /// </remarks>
    public Type ValueType { get; }

    // The .NET type that holds a value of this position with its null: ValueType, or
    // Nullable<> of it when the position is nullable and ValueType is a value type. What a
    // collection of a record holds at an inner position, and what a bound class declares.
    internal Type PositionType { get; }

    // The scalar type a value is held as: the underlying integer of an Enum, the key type of
    // the target of a Reference, the declared type of any other scalar. For an owned value
    // and a collection, the declared type.
    internal LatticeFieldTypeEnum ScalarType { get; }

    // How deep the declaration is, through collections and owned types together: 1 for a
    // scalar, a Binary, an Enum and a Reference; one more than its element for a List and a
    // Set; one more than the deeper of its key and its value for a Map; one more than the
    // deepest field of its owned type for an Owned. SchemaBuilder holds every field to its
    // bound.
    internal int Depth { get; }

    // How text at this position is compared: the comparison of a String, or of the key of the target
    // of a Reference, which compares as that key. Null for any other type. The one place where a
    // declared comparison gets its meaning, the one rule of equality and hash for text
    // (TextComparison): Ordinal is the equality of the code units, OrdinalIgnoreCase the case table of
    // Lattice's own. The comparers of a Map key, a Set element and the key of an entity in a write
    // batch take it from here, and so does the comparison of stored text in the engine,
    // Storage.Values.ValueComparer: the engine references the core, and the core cannot reference
    // the engine.
    internal TextComparison? TextComparison { get; }

    // The entity type a Reference refers to; null for any other type.
    internal ReferenceTarget? Target { get; }

    // How a record holds a List or a Set, or a Map; null for any other type.
    internal SequenceForm? Sequence { get; }

    internal MapForm? Map { get; }
}
