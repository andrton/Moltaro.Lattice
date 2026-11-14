using System.Globalization;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

// How a value that a caller gives by itself, a field value, a primary key or a part of a search
// key, is checked against the schema, and how stored values are kept separate from what a read
// returns. Values inside owned values and collections are RecordValues' business.
internal static class FieldValues
{
    // Returns the value in the field's exact .NET type. An integer of another width is
    // accepted when it fits, so that a literal such as 1 works for an Int64 field, for an Enum
    // field, which holds the integer of its enum type, and for a Reference, which holds the key
    // of its target.
    public static object Normalize(LatticeField field, object value, string parameterName)
    {
        var declaration = field.Declaration;
        var expected = declaration.ValueType;
        if (value.GetType() == expected)
        {
            return value;
        }

        if (FieldTypes.IsInteger(declaration.ScalarType) && value is sbyte or byte or short or ushort or int or uint or long or ulong)
        {
            try
            {
                return Convert.ChangeType(value, expected, CultureInfo.InvariantCulture);
            }
            catch (OverflowException)
            {
                // Reported below together with the other mismatches.
            }
        }

        throw new ArgumentException(
            $"Field '{field.Name}' is {field.Declaration.Type}; the {value.GetType().Name} value '{value}' cannot be used for it.",
            parameterName);
    }

    public static object NormalizeKey(LatticeEntityType entityType, object key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Normalize(entityType.Key, key, nameof(key));
    }

    // Stored values are never modified in place. Until owned values and collections are stored
    // a byte array is the only mutable value a stored entity holds, so it is copied whenever
    // values cross the database boundary.
    public static object?[] Copy(object?[] values)
    {
        var copy = (object?[])values.Clone();
        for (var index = 0; index < copy.Length; index++)
        {
            if (copy[index] is byte[] bytes)
            {
                copy[index] = bytes.Clone();
            }
        }

        return copy;
    }
}
