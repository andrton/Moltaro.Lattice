using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// An owned value without a .NET class: the values of the fields of its owned type, accessed
/// by field name, as a <see cref="LatticeRecord"/> holds those of an entity.
/// </summary>
/// <remarks>
/// <para>
/// A record holds one at a position declared <see cref="LatticeFieldTypeEnum.Owned"/>, and
/// accepts one of that owned type there, see <see cref="LatticeValueDeclaration.ValueType"/>.
/// It is part of the record that holds it: reading returns new ones with the record, and
/// changing one changes nothing in the database until the record that holds it is submitted
/// by a write. An owned value is not safe to change from several threads.
/// </para>
/// <para>
/// <b>Not implemented yet.</b> Records hold owned values, but storing them is not
/// implemented: every write of an entity type that has an Owned field throws
/// <see cref="NotSupportedException"/>.
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
    /// an owned value and a collection; an integer of another width is accepted when it
    /// fits.
    /// </summary>
    /// <param name="fieldName">The field name declared in the owned type.</param>
    /// <exception cref="ArgumentException">
    /// The owned type has no such field, or the value does not match the declaration of the
    /// field, as for a field of a <see cref="LatticeRecord"/>.
    /// </exception>
    public object? this[string fieldName]
    {
        get => throw new NotSupportedException("Owned values are not implemented yet.");
        set => throw new NotSupportedException("Owned values are not implemented yet.");
    }

    /// <summary>
    /// Gets the value of a field as <typeparamref name="T"/>; the default value when the
    /// field is <see langword="null"/>.
    /// </summary>
    /// <typeparam name="T">The .NET type of the field value.</typeparam>
    /// <param name="fieldName">The field name declared in the owned type.</param>
    /// <exception cref="ArgumentException">The owned type has no such field.</exception>
    /// <exception cref="InvalidCastException">The field value is not a <typeparamref name="T"/>.</exception>
    public T? Get<T>(string fieldName) =>
        throw new NotSupportedException("Owned values are not implemented yet.");
}
