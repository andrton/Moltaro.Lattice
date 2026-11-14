using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// An entity without a .NET class: the values of its fields, accessed by field name.
/// </summary>
/// <remarks>
/// <para>
/// A record is an owned copy, exactly like an entity object: reading returns a new record,
/// and changing a record changes nothing in the database until it is submitted by a write.
/// A record is not safe to change from several threads.
/// </para>
/// <para>
/// <b>What a record keeps of what it is given.</b> A collection is rebuilt: the record never
/// holds the caller's collection object, and the collections inside it are rebuilt in the
/// same way, at every depth, as the record's own <see cref="List{T}"/>,
/// <see cref="Dictionary{TKey, TValue}"/> and <see cref="HashSet{T}"/> with the comparers of
/// the schema. A later change of the caller's collection is not seen through the record.
/// </para>
/// <para>
/// <b>What a record returns is its own.</b> A collection it returns can be changed, and that
/// changes the record without a check. A record made invalid that way, such as by a
/// <see langword="null"/> added where none is allowed, is refused with
/// <see cref="LatticeErrorEnum.ValidationFailed"/> when it is written.
/// </para>
/// <para>
/// <b>A record is not the boundary of the database.</b> The boundary is a write, through a
/// set or by adding to a write batch, and a read. A write validates the record again and
/// copies it at every depth, owned values and byte arrays included: nothing changed in the
/// record afterwards reaches what was submitted. What a read returns shares nothing with the
/// database or with another read.
/// </para>
/// </remarks>
public sealed class LatticeRecord
{
    private readonly object?[] _values;

    /// <summary>
    /// Creates an empty record of the given entity type. Set its fields, then insert it.
    /// </summary>
    /// <param name="entityType">The entity type, taken from the database schema.</param>
    public LatticeRecord(LatticeEntityType entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        EntityType = entityType;
        _values = new object?[entityType.Fields.Count];
    }

    internal LatticeRecord(LatticeEntityType entityType, object?[] storedValues)
    {
        EntityType = entityType;
        _values = FieldValues.Copy(storedValues);
    }

    /// <summary>The entity type the record belongs to.</summary>
    public LatticeEntityType EntityType { get; }

    /// <summary>
    /// Gets or sets the value of a field. A value has the field's .NET type, see
    /// <see cref="LatticeValueDeclaration.ValueType"/>, which also says what is accepted for an
    /// owned value and a collection.
    /// </summary>
    /// <remarks>
    /// An integer of another width is accepted when it fits only where a value is given by
    /// itself: the value of a field here or in a <see cref="LatticeOwnedValue"/>, as a primary
    /// key and a part of a search key are. The element, key and value types of a collection
    /// are exact: a <c>List&lt;int&gt;</c> is not accepted for a List of Int64, nor a Map with
    /// <see cref="int"/> keys for a Map of Int64 keys.
    /// </remarks>
    /// <param name="fieldName">The field name declared in the schema.</param>
    /// <exception cref="ArgumentException">
    /// The entity type has no such field, or the value does not match the declaration of the
    /// field: it is of another type, an owned value of another owned type, or a collection
    /// whose element, key or value type is not exactly the declared one, that holds
    /// <see langword="null"/> where its declaration does not allow it, or that gives a Map key
    /// twice. The message names the field.
    /// </exception>
    public object? this[string fieldName]
    {
        get => _values[Field(fieldName).Ordinal];
        set
        {
            var field = Field(fieldName);
            _values[field.Ordinal] = value is null ? null : FieldValues.Normalize(field, value, nameof(value));
        }
    }

    /// <summary>
    /// Gets the value of a field as <typeparamref name="T"/>; the default value when the
    /// field is <see langword="null"/>.
    /// </summary>
    /// <typeparam name="T">The .NET type of the field value.</typeparam>
    /// <param name="fieldName">The field name declared in the schema.</param>
    /// <exception cref="ArgumentException">The entity type has no such field.</exception>
    /// <exception cref="InvalidCastException">The field value is not a <typeparamref name="T"/>.</exception>
    public T? Get<T>(string fieldName) => this[fieldName] is { } value ? (T)value : default;

    // Record -> values to store. The result shares nothing mutable with the record.
    internal object?[] Capture()
    {
        var values = FieldValues.Copy(_values);
        FieldValues.EnsureRequiredValues(EntityType, values);
        return values;
    }

    private LatticeField Field(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        return EntityType.TryGetField(fieldName, out var field)
            ? field
            : throw new ArgumentException($"Entity type '{EntityType.Name}' has no field '{fieldName}'.", nameof(fieldName));
    }
}
