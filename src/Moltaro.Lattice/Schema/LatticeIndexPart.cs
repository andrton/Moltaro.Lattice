namespace Moltaro.Lattice.Schema;

/// <summary>
/// One component of an index key: a field of the entity type, or a field inside an owned value
/// of it, reached through a path of fields.
/// </summary>
/// <remarks>
/// <para>
/// Every step of the path but the last is an <see cref="LatticeFieldTypeEnum.Owned"/> field.
/// A path never passes through a List, a Map or a Set: an index does not cover the members of
/// a collection, and a path through one is refused with
/// <see cref="LatticeErrorEnum.InvalidSchema"/>.
/// </para>
/// <para>
/// The field a part ends at, which for a plain part is the field itself, is one of three: a
/// scalar that is not <see cref="LatticeFieldTypeEnum.Binary"/>, an
/// <see cref="LatticeFieldTypeEnum.Enum"/>, a <see cref="LatticeFieldTypeEnum.Reference"/>. A
/// Reference is compared as the key of its target, with the comparison of that key. A part
/// that ends at a Binary or an Owned field, a List, a Map or a Set is refused with
/// <see cref="LatticeErrorEnum.InvalidSchema"/>, with the index and the field named: an owned
/// value is indexed through a path to a field inside it.
/// </para>
/// <para>
/// In the descriptor an element of the <c>Fields</c> of an index is one field identity or a
/// list of them, from the field of the entity type down to the indexed field. A list of one
/// identity is that field itself: <c>[[2]]</c> and <c>[2]</c> declare the same part, and the
/// index has the same fingerprint with either. An empty list is refused with
/// <see cref="LatticeErrorEnum.InvalidSchema"/>, with the index named.
/// </para>
/// <para>
/// When an owned value on the path is absent, the component has no value: it is
/// <see langword="null"/>, with the rules of a component without a value: a search for
/// <see langword="null"/> will find the entity, and a unique index will not compare it.
/// </para>
/// <para>
/// <b>Not implemented yet.</b> An entity type with an owned value or a Reference field cannot
/// be written yet, so a search through a part inside an owned value or of a Reference finds
/// nothing yet, see <see cref="LatticeIndex"/>. What this documentation says of them describes
/// how they will work.
/// </para>
/// </remarks>
public sealed class LatticeIndexPart
{
    internal LatticeIndexPart(LatticeField[] path)
    {
        Path = Array.AsReadOnly(path);
        Field = path[^1];

        // Every step but the last is an owned value, which is absent when it is nullable and
        // has no value; the last step is the indexed field.
        CanBeNull = path.Any(step => step.Declaration.IsNullable);
    }

    /// <summary>
    /// The fields from a field of the entity type down to the indexed field, in that order;
    /// one element for a field of the entity type itself.
    /// </summary>
    public IReadOnlyList<LatticeField> Path { get; }

    /// <summary>The indexed field: the last of the path.</summary>
    public LatticeField Field { get; }

    // Whether a value of this part can be null: the indexed field is nullable, or an owned
    // value on the path is.
    internal bool CanBeNull { get; }
}
