namespace Moltaro.Lattice.Schema;

/// <summary>
/// A secondary index of an entity type, as declared in the schema: an identity, a name, the
/// parts of its key, in order, and whether it is unique. Entities are found through it by the
/// values of the fields those parts cover: fields of the entity type, or fields inside its
/// owned values.
/// </summary>
/// <remarks>
/// <para>
/// An index is ordered by its first field, then by its second, and so on. It supports
/// equality on all of its fields, or on the leading ones followed by a range on the next.
/// An index on (Section, Name) therefore finds by Section, or by Section and Name, but
/// not by Name alone.
/// </para>
/// <para>
/// By default several entities may have the same values. A unique index allows a complete
/// set of values to at most one entity: the master refuses a write that would give it to a
/// second one. A set of values in which any field has no value is not complete, and is not
/// compared: any number of entities may have it.
/// </para>
/// <para>
/// How wide an index may be is a rule of the schema language, the same on every node. A node
/// of an index, at most 64 KiB, holds at least four of its widest entries. An entry takes 8
/// bytes, one byte for every eight parts that can be null, each part of a fixed-width type at
/// its width (a Boolean 1 byte; an integer, and an Enum by its underlying type, its width; a
/// Float32 and a DateOnly 4; a Float64, a DateTimeOffset, a TimeOnly and a TimeSpan 8; a
/// Decimal and a Guid 16; a Reference as the key of its target), and each String part 65 bytes
/// at the most. The widest entry of an index that has a String part may take 16,362 bytes, of
/// one that has none 16,364: a database whose schema declares a wider index does not open, and
/// the open fails with <see cref="LatticeErrorEnum.InvalidSchema"/>, with the index named, as
/// it does for an entity type whose fixed part does not fit one block. 251 String parts that
/// are never null fit; 252 do not.
/// </para>
/// </remarks>
public sealed class LatticeIndex
{
    internal LatticeIndex(int id, string name, LatticeIndexPart[] parts, bool isUnique)
    {
        Id = id;
        Name = name;
        Parts = Array.AsReadOnly(parts);
        IsUnique = isUnique;
    }

    /// <summary>
    /// The stable identity of the index within its entity type. It is declared in the
    /// descriptor, and a rename of the index keeps it.
    /// </summary>
    public int Id { get; }

    /// <summary>The index name, unique within its entity type.</summary>
    public string Name { get; }

    /// <summary>
    /// The parts of the index key, in the order that defines the index order: each a field of
    /// the entity type or a field inside an owned value of it, see
    /// <see cref="LatticeIndexPart"/>. An index names a part once: the same path twice, in
    /// either spelling of the descriptor, is refused with
    /// <see cref="LatticeErrorEnum.InvalidSchema"/>, with the index named, as the same field
    /// twice is. Two paths that differ in any step are different parts.
    /// </summary>
    public IReadOnlyList<LatticeIndexPart> Parts { get; }

    /// <summary>
    /// Whether the index is unique: at most one entity may have a given complete set of
    /// values of the indexed fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An insert, a replace or an upsert that would give a complete set of values to a second
    /// entity is refused with <see cref="LatticeErrorEnum.UniqueConstraintViolation"/>, which
    /// names the index in <see cref="LatticeException.IndexName"/>, and changes nothing; of two
    /// unique indexes it would break, the one declared first is named. Two values are the same
    /// when the comparison of every field holds them equal: a text by the comparison its field
    /// declares, every NaN one value and the two zeros of a floating-point field one value, an
    /// enum by its number. An empty text is a value. A write that keeps the values of its own
    /// entity, in the same form or in another one the comparison holds equal, is not refused.
    /// </para>
    /// <para>
    /// The master decides, for every write, also one submitted through a child, which does not
    /// reserve a value itself. Of writes of one value made at the same time at most one
    /// succeeds. A write of a value that another write, not completed yet, takes or gives up
    /// waits for that write to complete, and is then decided again.
    /// </para>
    /// </remarks>
    public bool IsUnique { get; }

    /// <summary>Returns the index name.</summary>
    public override string ToString() => Name;
}
