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
    /// declares in <see cref="LatticeSchema.StringEncoding"/>, at every depth of owned values and
    /// Lists: the bound holds for each such value, a field of the entity, a field of an owned value
    /// and an element of a List, not only for a field of the entity. A write of a larger value, and adding an entity with one
    /// to a write batch, is refused with <see cref="LatticeErrorEnum.ValidationFailed"/>, with its
    /// place named, as every other validation of an entity names one: the field of the entity or of
    /// the owned type that declares it, and for an element the field that holds the List; before the
    /// conditions of the write are looked at and before a write is refused as not implemented. The bound is a fixed rule of the storage format,
    /// the same on every node and not an option; an entity itself has no size limit, only the
    /// memory of the instance bounds it.
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
    /// One value takes at most 268,173,376 bytes, at every depth of owned values and Lists: the bound
    /// holds for each such value, a field of the entity, a field of an owned value and an element of a
    /// List, not only for a field of the entity. A write of a larger value, and adding an entity with one to a write
    /// batch, is refused with <see cref="LatticeErrorEnum.ValidationFailed"/>, with its place named,
    /// as for a String; before the conditions of the write are looked at and before a write is
    /// refused as not implemented. The bound is a fixed rule of the storage format,
    /// the same on every node and not an option; an entity itself has no size limit, only the
    /// memory of the instance bounds it.
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
    /// <see cref="LatticeEntityType"/> counts it: a List whose elements hold no String, Binary or
    /// List holds 268,173,376 bytes of them, 33,521,672 elements of Int64; one whose elements hold
    /// one holds 4,094 times the elements one chunk holds whole, each with room for 64 bytes of each
    /// String, Binary and List it holds, 3,348,892 elements of String. A write of a larger List, at
    /// any depth, of an entity type the engine stores, and adding such an entity to a write batch, is
    /// refused with <see cref="LatticeErrorEnum.ValidationFailed"/>, with its place named as for a
    /// String, before the conditions of the write are looked at; the bound follows from the layout of
    /// the entity type, so a write of an entity type the engine does not store ends in
    /// <see cref="NotSupportedException"/> without it.
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
    /// <b>Not implemented yet.</b> A schema declares Maps, and is compiled, saved and loaded
    /// with them, and records and bound classes hold them, but storing them is not
    /// implemented. Until then no write of an entity type that has a Map field is carried
    /// out: after the checks every write makes of its arguments and of the entity against the
    /// schema, it throws <see cref="NotSupportedException"/>.
    /// </remarks>
    Map = 23,

    /// <summary>
    /// Distinct values of one declaration, see <see cref="LatticeValueDeclaration.Element"/>:
    /// two members are the same member when the element declaration holds them equal. The
    /// order in which a Set is enumerated is not guaranteed.
    /// </summary>
    /// <remarks>
    /// <b>Not implemented yet.</b> A schema declares Sets, and is compiled, saved and loaded
    /// with them, and records and bound classes hold them, but storing them is not
    /// implemented. Until then no write of an entity type that has a Set field is carried
    /// out: after the checks every write makes of its arguments and of the entity against the
    /// schema, it throws <see cref="NotSupportedException"/>.
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
