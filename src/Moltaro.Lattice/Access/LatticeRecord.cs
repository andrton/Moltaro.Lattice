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
/// <b>What a record keeps of what it is given.</b> A record copies every value that is set
/// into it, at every depth, and holds nothing of the caller's. A collection is rebuilt as the
/// record's own <see cref="List{T}"/>, <see cref="Dictionary{TKey, TValue}"/> or
/// <see cref="HashSet{T}"/> with the comparers of the schema, and so are the collections
/// inside it. A <see cref="LatticeOwnedValue"/> is copied field by field, as if each of its
/// fields were set again, so what it holds is copied and checked in the same way. A byte
/// array is copied. A change made afterwards to the value that was given, to the
/// collection, the owned value or the array, is not seen through the record, and one owned
/// value set into two records, or twice into one collection, gives independent copies.
/// </para>
/// <para>
/// <b>What a record returns is its own, and the same object every time:</b> the collection,
/// the owned value or the byte array it holds. A change made through it is a change of the
/// record, and that is how a value inside a record is edited after it was set:
/// <c>record.Get&lt;LatticeOwnedValue&gt;("Home")["City"] = "Lviv"</c>. A change made
/// through a returned collection is not checked when it is made. A record made invalid that
/// way, such as by a <see langword="null"/> added where none is allowed, is refused with
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
    /// owned value and a collection. Getting returns what the record holds, the same object
    /// every time; setting stores a copy of the value, as the remarks of the class say.
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
    /// twice. An owned value is checked as if each of its fields were set again, so one made
    /// invalid through a collection it returned is refused here. The message names the field
    /// whose value does not match its declaration: for an owned value, the field of the owned
    /// value, as if that field were set by itself.
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
    /// field is <see langword="null"/>. It returns what the indexer returns: a collection, an
    /// owned value or a byte array is the object the record holds, the same every time.
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
