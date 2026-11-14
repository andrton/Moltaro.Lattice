namespace Moltaro.Lattice.Schema;

/// <summary>
/// A secondary index of an entity type, as declared in the schema: an identity, a name, the
/// fields it covers, in order, and whether it is unique. Entities are found through it by
/// the values of those fields.
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
/// </remarks>
public sealed class LatticeIndex
{
    internal LatticeIndex(string name, IReadOnlyList<LatticeField> fields, bool isUnique)
    {
        Name = name;
        Fields = fields;
        IsUnique = isUnique;
    }

    /// <summary>
    /// The stable identity of the index within its entity type. It is declared in the
    /// descriptor, and a rename of the index keeps it.
    /// </summary>
    public int Id => throw new NotSupportedException("The identity of an index is not implemented yet.");

    /// <summary>The index name, unique within its entity type.</summary>
    public string Name { get; }

    /// <summary>The indexed fields, in the order that defines the index order.</summary>
    public IReadOnlyList<LatticeField> Fields { get; }

    /// <summary>
    /// Whether the index is unique: at most one entity may have a given complete set of
    /// values of the indexed fields.
    /// </summary>
    public bool IsUnique { get; }

    /// <summary>Returns the index name.</summary>
    public override string ToString() => Name;
}
