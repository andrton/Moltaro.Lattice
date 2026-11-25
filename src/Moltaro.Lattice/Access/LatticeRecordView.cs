using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// One written state of an entity, read in place for the duration of its synchronous viewer.
/// </summary>
/// <remarks>All fields of this view come from that same state, including while a later write is published.</remarks>
public readonly ref struct LatticeRecordView
{
    /// <summary>The schema's declaration of this entity type.</summary>
    public LatticeEntityType EntityType => throw new NotSupportedException();

    /// <summary>The revision of the entity state held by this view.</summary>
    public LatticeRevision Revision => throw new NotSupportedException();

    /// <summary>Finds and views one field by its declared name, compared ordinally and case-sensitively.</summary>
    /// <param name="fieldName">The name of one field directly on this entity type.</param>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">The name contains a dot or is absent from this entity type.</exception>
    public LatticeValueView this[string fieldName] => throw new NotSupportedException();

    /// <summary>Views a field using its schema handle, without looking up its name.</summary>
    /// <param name="field">The exact field of this entity type in this schema.</param>
    /// <exception cref="ArgumentNullException">The field is null.</exception>
    /// <exception cref="ArgumentException">The field is from another type or schema.</exception>
    public LatticeValueView this[LatticeField field] => throw new NotSupportedException();
}
