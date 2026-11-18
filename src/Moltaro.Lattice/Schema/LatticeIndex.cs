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
/// <b>Not implemented yet.</b> An index is declared, compiled, saved and loaded, but finding
/// entities through it is not implemented, as <c>ILatticeIndex&lt;TEntity&gt;</c> states, and
/// neither is keeping a unique index, as <see cref="IsUnique"/> states. What this
/// documentation and that of <see cref="LatticeIndexPart"/> say of a search and of the
/// comparison of a unique index describes how they will work.
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
    /// <b>Not implemented yet.</b> A schema declares unique indexes, and is compiled, saved
    /// and loaded with them, but keeping them is not implemented. Until then no write of an
    /// entity type that declares a unique index, an insert, a replace, an upsert or a
    /// delete, is carried out: after the checks every write makes of its arguments and of
    /// the entity against the schema, it throws <see cref="NotSupportedException"/>.
    /// </remarks>
    public bool IsUnique { get; }

    /// <summary>Returns the index name.</summary>
    public override string ToString() => Name;
}
