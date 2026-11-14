using System.Globalization;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

// How values supplied by callers are checked against the schema, and how they are kept
// separate from what the database stores.
internal static class FieldValues
{
    // Returns the value in the field's exact .NET type. An integer of another width is
    // accepted when it fits, so that a literal such as 1 works for an Int64 field.
    public static object Normalize(LatticeField field, object value, string parameterName)
    {
        var expected = field.Declaration.ValueType;
        if (value.GetType() == expected)
        {
            return value;
        }

        if (FieldTypes.IsInteger(field.Declaration.Type) && value is sbyte or byte or short or ushort or int or uint or long or ulong)
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

    // Stored values are never modified in place; a byte array is the only mutable value,
    // so it is copied whenever values cross the database boundary.
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

    public static void EnsureRequiredValues(LatticeEntityType entityType, object?[] values)
    {
        foreach (var field in entityType.Fields)
        {
            if (values[field.Ordinal] is null && !field.Declaration.IsNullable)
            {
                throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"{entityType.Name}.{field.Name} is required and has no value.");
            }
        }
    }
}
