namespace Moltaro.Lattice.Schema;

/// <summary>
/// Type of a value declared in the schema: of a field, or of the element, the key or the value
/// of a collection. The names are the ones used in the schema descriptor.
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
    /// <remarks>
    /// One value takes at most 268,173,376 bytes of its stored encoding, the encoding the schema
    /// declares in <see cref="LatticeSchema.StringEncoding"/>. The bound holds at every depth
    /// of owned values and collections, including keys, values and members. A write of a larger
    /// String is refused before its conditions with <see cref="LatticeErrorEnum.ValidationFailed"/>,
    /// as is adding an entity with one to a write batch. The error names the declaring field
    /// or the field that holds the collection. The bound is a fixed rule of the storage format
    /// on every node. The size of the entity itself is bounded by the memory of the instance.
    /// </remarks>
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
    /// <remarks>
    /// One value takes at most 268,173,376 bytes at every depth of owned values and collections.
    /// A write of a larger Binary is refused before its conditions with
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>, as is adding an entity with one to a
    /// write batch. The error names its field as for a String. The bound is a fixed rule of the
    /// storage format on every node. The size of the entity itself is bounded by the memory
    /// of the instance.
    /// </remarks>
    Binary = 19,

    /// <summary>
    /// A value of an enum type declared in the schema, see
    /// <see cref="LatticeValueDeclaration.EnumType"/>. It is stored as its underlying integer,
    /// and its .NET type is that integer; a bound class may declare the property as a .NET
    /// enum that agrees with the enum type, as <see cref="ILatticeDatabase.Set{TEntity}"/>
    /// states.
    /// </summary>
    Enum = 20,

    /// <summary>
    /// An owned value: a value of an owned type declared in the schema, see
    /// <see cref="LatticeValueDeclaration.OwnedType"/>, with fields of its own. It has no key
    /// and is never shared: it belongs to the one entity that holds it, and it is written and
    /// read with that entity. At every position a record holds it as a <c>LatticeOwnedValue</c>,
    /// see <see cref="LatticeValueDeclaration.ValueType"/>, and a bound class as an object of
    /// the class that its owned type names, see <see cref="ILatticeDatabase.Set{TEntity}"/>.
    /// </summary>
    Owned = 21,

    /// <summary>
    /// An ordered sequence of values of one declaration, see
    /// <see cref="LatticeValueDeclaration.Element"/>. It keeps the order and the duplicates of
    /// the values it was given. An element may be anything a field may be, an owned value and
    /// another collection included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A List holds at most what one directory of 4,094 chunks of 65,504 bytes holds, a fixed rule
    /// of the storage format, the same on every node and not an option, as the bound of one String
    /// or Binary value is. It follows from what one element stores at a fixed width, as
    /// <see cref="LatticeEntityType"/> counts it. Without String, Binary or collection descriptors
    /// the chunks hold 268,173,376 bytes of entries, for example 33,521,672 elements of Int64.
    /// With descriptors each chunk holds whole entries and reserves 64 bytes per descriptor,
    /// for example 3,348,892 elements of String across all chunks. A write of a larger List at
    /// any depth, including when adding an entity to a write batch, is
    /// refused with <see cref="LatticeErrorEnum.ValidationFailed"/>, with its place named as for a
    /// String, before the conditions of the write are looked at.
    /// </para>
    /// </remarks>
    List = 22,

    /// <summary>
    /// Values by key: at most one value for each key, see
    /// <see cref="LatticeValueDeclaration.Key"/> and <see cref="LatticeValueDeclaration.Value"/>.
    /// Two keys are the same key when the key declaration holds them equal. The order in which
    /// a Map is enumerated is not guaranteed.
    /// </summary>
    /// <remarks>
    /// A Map key is required. A write with equal keys is refused with
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>. The entry limit is the smaller of
    /// 23,465,170 and what the <see cref="List"/> bound holds with the key and value in
    /// one entry. A Map above that limit at any depth is refused with the same error,
    /// with its field named, including when adding an entity to a write batch.
    /// </remarks>
    Map = 23,

    /// <summary>
    /// Distinct values of one declaration, see <see cref="LatticeValueDeclaration.Element"/>:
    /// two members are the same member when the element declaration holds them equal. The
    /// order in which a Set is enumerated is not guaranteed.
    /// </summary>
    /// <remarks>
    /// From equal members a write keeps the first in the order the input collection gives
    /// them. The member limit is the smaller of 23,465,170 and what the <see cref="List"/>
    /// bound holds for the element declaration. A Set above that limit at any depth is refused
    /// with <see cref="LatticeErrorEnum.ValidationFailed"/>, with its field named, including
    /// when adding an entity to a write batch.
    /// </remarks>
    Set = 24,

    /// <summary>
    /// A reference to an entity of the entity type that the declaration names, see
    /// <see cref="LatticeValueDeclaration.ReferencedEntityTypeName"/>, held as the primary key
    /// of the target in the .NET type of that key. A reference does not own its target, and
    /// nothing checks that the target exists: an entity with that key may be absent, and
    /// deleting the target does not change the reference.
    /// </summary>
    /// <remarks>
    /// A reference whose target has a String key holds text, and a write validates it as a String
    /// value: text that is not well-formed, with a surrogate without its pair, and text above the
    /// bound of one value, see <see cref="String"/>, are refused with
    /// <see cref="LatticeErrorEnum.ValidationFailed"/>, with its place named, at every depth.
    /// </remarks>
    Reference = 25,
}
