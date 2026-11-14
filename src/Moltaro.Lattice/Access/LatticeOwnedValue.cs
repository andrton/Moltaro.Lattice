using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// An owned value without a .NET class: the values of the fields of its owned type, accessed
/// by field name, as a <see cref="LatticeRecord"/> holds those of an entity.
/// </summary>
/// <remarks>
/// <para>
/// A record holds one at every position declared <see cref="LatticeFieldTypeEnum.Owned"/>, in
/// a field, an element of a List or a value of a Map, whether or not the owned type names a
/// class, and accepts one of that owned type there, see
/// <see cref="LatticeValueDeclaration.ValueType"/>. A bound class never holds one, see
/// <see cref="ILatticeDatabase.Set{TEntity}"/>.
/// </para>
/// <para>
/// Setting an owned value into a record, into another owned value or into a collection
/// copies it, as the remarks of <see cref="LatticeRecord"/> say: a change made to it
/// afterwards is not seen through the record. An owned value treats what is set into it in
/// the same way as a record, copying every value at every depth, and returns in the same
/// way: its own collection, owned value or byte array, the same object every time, and a
/// change made through it is a change of the owned value. A change made through a returned
/// collection is not checked when it is made; an owned value made invalid that way is
/// refused when it is set, see <see cref="this[string]"/>.
/// </para>
/// <para>
/// Reading returns new owned values with the record, and changing one changes nothing in
/// the database until the record that holds it is submitted by a write. An owned value is
/// not safe to change from several threads.
/// </para>
/// <para>
/// <b>Not implemented yet.</b> Records hold owned values, but storing them is not
/// implemented. Until then no write of an entity type that has an Owned field is carried
/// out: after the checks every write makes of its arguments and of the entity against the
/// schema, it throws <see cref="NotSupportedException"/>.
/// </para>
/// </remarks>
public sealed class LatticeOwnedValue
{
    /// <summary>
    /// Creates an empty owned value of the given owned type. Set its fields, then set it at a
    /// position of that owned type in a record.
    /// </summary>
    /// <param name="ownedType">The owned type, taken from the database schema.</param>
    public LatticeOwnedValue(LatticeOwnedType ownedType)
    {
        ArgumentNullException.ThrowIfNull(ownedType);
        OwnedType = ownedType;
    }

    /// <summary>The owned type the value belongs to.</summary>
    public LatticeOwnedType OwnedType { get; }

    /// <summary>
    /// Gets or sets the value of a field. A value has the field's .NET type, see
    /// <see cref="LatticeValueDeclaration.ValueType"/>, which also says what is accepted for
    /// an owned value and a collection. An integer of another width is accepted when it fits,
    /// and the types inside a collection are exact, as for a field of a
    /// <see cref="LatticeRecord"/>. Getting returns what the owned value holds, the same object
    /// every time; setting stores a copy of the value.
    /// </summary>
    /// <param name="fieldName">The field name declared in the owned type.</param>
    /// <exception cref="ArgumentException">
    /// The owned type has no such field, or the value does not match the declaration of the
    /// field, as for a field of a <see cref="LatticeRecord"/>: an owned value set here that
    /// was made invalid through a collection it returned is refused, with its field named.
    /// </exception>
    public object? this[string fieldName]
    {
        get => throw new NotSupportedException("Owned values are not implemented yet.");
        set => throw new NotSupportedException("Owned values are not implemented yet.");
    }

    /// <summary>
    /// Gets the value of a field as <typeparamref name="T"/>; the default value when the
    /// field is <see langword="null"/>. It returns what the indexer returns: a collection, an
    /// owned value or a byte array is the object the owned value holds, the same every time.
    /// </summary>
    /// <typeparam name="T">The .NET type of the field value.</typeparam>
    /// <param name="fieldName">The field name declared in the owned type.</param>
    /// <exception cref="ArgumentException">The owned type has no such field.</exception>
    /// <exception cref="InvalidCastException">The field value is not a <typeparamref name="T"/>.</exception>
    public T? Get<T>(string fieldName) =>
        throw new NotSupportedException("Owned values are not implemented yet.");
}
