using Moltaro.Lattice.Access.Values;
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
/// Setting an owned value into a record or into another owned value, by itself or inside a
/// collection that is set, copies it, as the remarks of <see cref="LatticeRecord"/> say: a
/// change made to it afterwards is not seen through the record. An owned value added to a
/// collection that a record or an owned value returned is not copied then; the write
/// validates and copies it. An owned value treats what is set into it in
/// the same way as a record, copying every value at every depth, and returns in the same
/// way: its own collection, owned value or byte array, the same object every time, and a
/// change made through it is a change of the owned value. A change made through a returned
/// collection is not checked when it is made; an owned value made invalid that way is
/// refused when it is set, see <see cref="this[string]"/>.
/// </para>
/// <para>
/// Changing an owned value changes nothing in the database until the record that holds it
/// is submitted by a write. A read returns new ones with the record, at every depth. An owned
/// value is not safe to change from several threads.
/// </para>
/// <para>
/// <b>Not implemented yet</b> for every use: an owned value is stored and read back by name,
/// and the rest is not built yet, as the remarks of <see cref="LatticeFieldTypeEnum.Owned"/> say.
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
        Values = new object?[ownedType.FieldsByOrdinal.Length];
    }

    /// <summary>The owned type the value belongs to.</summary>
    public LatticeOwnedType OwnedType { get; }

    // One value per field of the owned type, by the ordinal of the field, in the form a record
    // holds; null for a field without a value. RecordValues and ClassBinder fill it when they
    // make the copy that a record holds.
    internal object?[] Values { get; }

    /// <summary>
    /// Gets or sets the value of a field. A value has the field's .NET type, see
    /// <see cref="LatticeValueDeclaration.ValueType"/>, which also says what is accepted for
    /// an owned value and a collection. Getting returns what the owned value holds, the same object
    /// every time; setting stores a copy of the value.
    /// </summary>
    /// <remarks>
    /// A value is taken by the rules of a field of a record, see
    /// <see cref="LatticeRecord.this[string]"/>: a number of another type by the one rule of a part
    /// of a search key, see <see cref="IndexKey"/>, held as the .NET type of the field, and a
    /// collection only with the declared types.
    /// </remarks>
    /// <param name="fieldName">The field name declared in the owned type.</param>
    /// <exception cref="ArgumentException">
    /// The owned type has no such field, or the value does not match the declaration of the
    /// field, as for a field of a <see cref="LatticeRecord"/>: an owned value set here that
    /// was made invalid through a collection it returned is refused, with its field named.
    /// </exception>
    public object? this[string fieldName]
    {
        get => Values[Field(fieldName).Ordinal];
        set
        {
            var field = Field(fieldName);
            Values[field.Ordinal] = value is null ? null : RecordValues.Accept(field, OwnedType.Name, value);
        }
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
    public T? Get<T>(string fieldName) => this[fieldName] is { } value ? (T)value : default;

    private LatticeField Field(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        return OwnedType.TryGetField(fieldName, out var field)
            ? field
            : throw new ArgumentException($"Owned type '{OwnedType.Name}' has no field '{fieldName}'.", nameof(fieldName));
    }
}
