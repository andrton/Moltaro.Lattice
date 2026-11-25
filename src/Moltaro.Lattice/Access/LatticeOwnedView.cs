using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>An owned value read in place for the duration of the enclosing entity's viewer.</summary>
public readonly ref struct LatticeOwnedView
{
    /// <summary>The schema's declaration of this owned type.</summary>
    public LatticeOwnedType OwnedType => throw new NotSupportedException();

    /// <summary>Finds and views one field by its declared name, compared ordinally and case-sensitively.</summary>
    /// <param name="fieldName">The name of one field directly on this owned type.</param>
    /// <exception cref="ArgumentNullException">The name is null.</exception>
    /// <exception cref="ArgumentException">The name contains a dot or is absent from this owned type.</exception>
    public LatticeValueView this[string fieldName] => throw new NotSupportedException();

    /// <summary>Views a field using its schema handle, without looking up its name.</summary>
    /// <param name="field">The exact field of this owned type in this schema.</param>
    /// <exception cref="ArgumentNullException">The field is null.</exception>
    /// <exception cref="ArgumentException">The field is from another type or schema.</exception>
    public LatticeValueView this[LatticeField field] => throw new NotSupportedException();
}
