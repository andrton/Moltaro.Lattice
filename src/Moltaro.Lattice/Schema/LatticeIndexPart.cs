namespace Moltaro.Lattice.Schema;

/// <summary>
/// One component of an index key: a field of the entity type, or a field inside an owned value
/// of it, reached through a path of fields.
/// </summary>
/// <remarks>
/// <para>
/// Every step of the path but the last is an <see cref="LatticeFieldTypeEnum.Owned"/> field.
/// A path never passes through a List, a Map or a Set: an index does not cover the members of
/// a collection. In the descriptor such a part is written in the <c>Fields</c> of the index
/// as a list of field identities, from the field of the entity type down to the indexed
/// field, instead of one identity.
/// </para>
/// <para>
/// When an owned value on the path is absent, the component has no value: it is
/// <see langword="null"/>, with the rules of a component without a value. A search for
/// <see langword="null"/> finds the entity, and a unique index does not compare it.
/// </para>
/// </remarks>
public sealed class LatticeIndexPart
{
    internal LatticeIndexPart(IReadOnlyList<LatticeField> path)
    {
        Path = path;
        Field = path[^1];
    }

    /// <summary>
    /// The fields from a field of the entity type down to the indexed field, in that order;
    /// one element for a field of the entity type itself.
    /// </summary>
    public IReadOnlyList<LatticeField> Path { get; }

    /// <summary>The indexed field: the last of the path.</summary>
    public LatticeField Field { get; }
}
