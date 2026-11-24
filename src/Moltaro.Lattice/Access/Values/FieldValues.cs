using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Values;

// How a value that a caller gives by itself, a field value or a primary key, is checked against
// the schema. Values inside owned values and collections are RecordValues' business; a value of a
// key or a bound to search an index with is IndexValue.Bind's, which takes the check of text from
// here.
internal static class FieldValues
{
    // Returns the value in the field's exact .NET type: a value of that type as it is, and a number of
    // another type as a part of a search key takes it, by the same code (IndexValue.TryConvertTo; the
    // remarks of IndexKey): an integer of another width that fits, any integer for a Decimal, an integer
    // that a Float32 or a Float64 holds exactly, a Float32 for a Float64, and a Float64 for a Float32 when
    // it converts back to itself. So a literal such as 1 works for an Int64 field, for a Decimal field, for
    // an Enum field, which holds the integer of its enum type, and for a Reference, which holds the key of
    // its target. Text that is not well-formed and an integer that an enum type does not allow are the
    // checks of the write. For the types a primary key may have, the integers, String and Guid, the rule is
    // that of a key given as an argument.
    public static object Normalize(LatticeField field, object value, string parameterName)
    {
        var declaration = field.Declaration;
        if (value.GetType() == declaration.ValueType)
        {
            return value;
        }

        return IndexValue.Of(value).TryConvertTo(declaration.ScalarType, out var converted)
            ? converted.ToObject()!
            : throw CannotBeUsed(field, value, parameterName);
    }

    // The refusal of a value, not null, that cannot be used for the field, for the parameter named: of a
    // value given by itself here, and of a value of an index key or a bound (IndexValue.Bind).
    public static ArgumentException CannotBeUsed(LatticeField field, object value, string parameterName) =>
        new($"Field '{field.Name}' is {field.Declaration.Type}; the {value.GetType().Name} value '{value}' cannot be used for it.", parameterName);

    public static object NormalizeKey(LatticeEntityType entityType, object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return NormalizeArgument(entityType.Key, key, nameof(key));
    }

    // A value given as an argument to look an entity up with by its primary key. Normalized as
    // above, and text that is not well-formed is refused as a value of another type is, since no
    // stored value can be equal to it, so a lookup by it could only find nothing. A value set into a
    // record is not checked for it; the write checks it.
    public static object NormalizeArgument(LatticeField field, object value, string parameterName)
    {
        var normalized = Normalize(field, value, parameterName);

        if (normalized is string text)
        {
            ThrowIfNotWellFormed(field, text, parameterName);
        }

        return normalized;
    }

    // A String key given as an argument that is not well-formed text: ArgumentException, the
    // message of a value that cannot be used for the field.
    public static void ThrowIfNotWellFormed(LatticeField field, string text, string parameterName)
    {
        if (!WellFormedText.IsWellFormed(text))
        {
            throw new ArgumentException(
                $"Field '{field.Name}' is {field.Declaration.Type}; the value '{text}' is not well-formed text, it has a surrogate without its pair, and cannot be used for it.",
                parameterName);
        }
    }
}
