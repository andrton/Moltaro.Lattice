using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

/// <summary>
/// An entity without a .NET class: the values of its fields, accessed by field name.
/// </summary>
/// <remarks>
/// A record is an owned copy, exactly like an entity object: reading returns a new record,
/// and changing a record changes nothing in the database until it is submitted by a write.
/// A record is not safe to change from several threads.
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
    /// owned value and a collection; an integer of another width is accepted when it fits.
    /// </summary>
    /// <param name="fieldName">The field name declared in the schema.</param>
    /// <exception cref="ArgumentException">
    /// The entity type has no such field, or the value does not match the declaration of the
    /// field: it is of another type, an owned value of another owned type, or a collection
    /// with an element, a key or a value of another type or <see langword="null"/> where its
    /// declaration does not allow it, or with a key given twice.
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
