using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>An owned value read in place for the duration of the enclosing entity's viewer.</summary>
public readonly ref struct LatticeOwnedView
{
    private readonly IViewStorage _storage;
    private readonly ViewRecord _row;
    private readonly int _position;

    internal LatticeOwnedView(IViewStorage storage, ViewRecord row, int position, LatticeOwnedType ownedType)
    {
        _storage = storage;
        _row = row;
        _position = position;
        OwnedType = ownedType;
    }

    /// <summary>The schema's declaration of this owned type.</summary>
    public LatticeOwnedType OwnedType { get; }

    /// <summary>Finds and views one field by its declared name, compared ordinally and case-sensitively.</summary>
    /// <param name="fieldName">The name of one field directly on this owned type.</param>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">The name contains a dot or is absent from this owned type.</exception>
    public LatticeValueView this[string fieldName]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(fieldName);
            if (!OwnedType.TryGetField(fieldName, out var field))
            {
                throw new ArgumentException($"Owned type '{OwnedType.Name}' has no field '{fieldName}'.", nameof(fieldName));
            }

            return Child(field);
        }
    }

    /// <summary>Views a field using its schema handle, without looking up its name.</summary>
    /// <param name="field">The exact field of this owned type in this schema.</param>
    /// <exception cref="ArgumentNullException">The field is null.</exception>
    /// <exception cref="ArgumentException">The field is from another type or schema.</exception>
    public LatticeValueView this[LatticeField field]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(field);
            field.OrdinalIn(OwnedType.FieldsByOrdinal);
            return Child(field);
        }
    }

    private LatticeValueView Child(LatticeField field) =>
        new(_storage, _row, _storage.ChildPosition(_row, _position, field), field.Declaration);
}
