using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// One written state of an entity, read in place for the duration of its synchronous viewer.
/// </summary>
/// <remarks>All fields of this view come from that same state, including while a later write is published.</remarks>
public readonly ref struct LatticeRecordView
{
    private readonly IViewStorage _storage;
    private readonly ViewRecord _row;

    internal LatticeRecordView(IViewStorage storage, ViewRecord row, LatticeEntityType entityType, LatticeRevision revision)
    {
        _storage = storage;
        _row = row;
        EntityType = entityType;
        Revision = revision;
    }

    /// <summary>The schema's declaration of this entity type.</summary>
    public LatticeEntityType EntityType { get; }

    /// <summary>The revision of the entity state held by this view.</summary>
    public LatticeRevision Revision { get; }

    /// <summary>Finds and views one field by its declared name, compared ordinally and case-sensitively.</summary>
    /// <param name="fieldName">The name of one field directly on this entity type.</param>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">The name contains a dot or is absent from this entity type.</exception>
    public LatticeValueView this[string fieldName]
    {
        get
        {
            var field = EntityType.GetField(fieldName);
            return new LatticeValueView(_storage, _row, field.Ordinal, field.Declaration);
        }
    }

    /// <summary>Views a field using its schema handle, without looking up its name.</summary>
    /// <param name="field">The exact field of this entity type in this schema.</param>
    /// <exception cref="ArgumentNullException">The field is null.</exception>
    /// <exception cref="ArgumentException">The field is from another type or schema.</exception>
    public LatticeValueView this[LatticeField field]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(field);
            var position = field.OrdinalIn(EntityType.FieldsByOrdinal);
            return new LatticeValueView(_storage, _row, position, field.Declaration);
        }
    }
}
