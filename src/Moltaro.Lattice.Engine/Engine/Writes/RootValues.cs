using System.Diagnostics;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// Sets the fields of a root from the values a write took of an object or a record: one value for
// each field, by LatticeField.Ordinal, in the form LatticeValueDeclaration.ValueType gives. The
// core took them and checked them against the schema (EntityBinding.Capture, LatticeRecord.Capture),
// so every value has the type its field needs, a field without a value is nullable, its text is
// well-formed, its value within the bound of one value and the value of an Enum field one its enum
// type declares. The root writer checks the text and the bound again as the guard of the stored form,
// and reports itself what it refuses. An Enum field holds the integer of the underlying type of its
// enum type, and is written as that integer type (LatticeValueDeclaration.ScalarType).
internal static class RootValues
{
    // The writer is passed by reference: it is never copied (RootWriter).
    public static void Write(ref RootWriter writer, LatticeEntityType entityType, object?[] values)
    {
        var fields = entityType.Fields;

        // By index: a foreach over the list of the schema would allocate its enumerator.
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            var value = values[field.Ordinal];

            // A nullable field that is not set is null.
            if (value is null)
            {
                continue;
            }

            switch (field.Declaration.ScalarType)
            {
                case LatticeFieldTypeEnum.Boolean:
                    writer.SetBoolean(field, (bool)value);
                    break;
                case LatticeFieldTypeEnum.Int8:
                    writer.SetInteger(field, (sbyte)value);
                    break;
                case LatticeFieldTypeEnum.Int16:
                    writer.SetInteger(field, (short)value);
                    break;
                case LatticeFieldTypeEnum.Int32:
                    writer.SetInteger(field, (int)value);
                    break;
                case LatticeFieldTypeEnum.Int64:
                    writer.SetInteger(field, (long)value);
                    break;
                case LatticeFieldTypeEnum.UInt8:
                    writer.SetUnsigned(field, (byte)value);
                    break;
                case LatticeFieldTypeEnum.UInt16:
                    writer.SetUnsigned(field, (ushort)value);
                    break;
                case LatticeFieldTypeEnum.UInt32:
                    writer.SetUnsigned(field, (uint)value);
                    break;
                case LatticeFieldTypeEnum.UInt64:
                    writer.SetUnsigned(field, (ulong)value);
                    break;
                case LatticeFieldTypeEnum.Float32:
                    writer.SetFloat32(field, (float)value);
                    break;
                case LatticeFieldTypeEnum.Float64:
                    writer.SetFloat64(field, (double)value);
                    break;
                case LatticeFieldTypeEnum.Decimal:
                    writer.SetDecimal(field, (decimal)value);
                    break;
                case LatticeFieldTypeEnum.Guid:
                    writer.SetGuid(field, (Guid)value);
                    break;
                case LatticeFieldTypeEnum.DateTimeOffset:
                    writer.SetDateTimeOffset(field, (DateTimeOffset)value);
                    break;
                case LatticeFieldTypeEnum.DateOnly:
                    writer.SetDateOnly(field, (DateOnly)value);
                    break;
                case LatticeFieldTypeEnum.TimeOnly:
                    writer.SetTimeOnly(field, (TimeOnly)value);
                    break;
                case LatticeFieldTypeEnum.TimeSpan:
                    writer.SetTimeSpan(field, (TimeSpan)value);
                    break;
                case LatticeFieldTypeEnum.String:
                    writer.SetString(field, (string)value);
                    break;
                case LatticeFieldTypeEnum.Binary:
                    writer.SetBinary(field, (byte[])value);
                    break;
                default:
                    // An owned value and a collection keep their entity type from being stored, as a
                    // reference does, so no write of one comes here.
                    throw new UnreachableException($"The field {entityType.Name}.{field.Name} is {field.Declaration.Type}, which the engine does not store.");
            }
        }
    }

    // Reads every field of the root into the values, by LatticeField.Ordinal, in the form a write takes
    // and a record holds (LatticeValueDeclaration.ValueType): null for a field that is null, a scalar
    // boxed in its exact type — for an Enum field the integer of the underlying type of its enum type —
    // and a new string or array for a String or Binary value. The reverse of Write, for a read that copies
    // a root as a record (Sets.RecordCopy) and for a forwarded write a master writes again
    // (WriteExecutor).
    public static void Read(in RootReader reader, LatticeEntityType entityType, object?[] values)
    {
        var fields = entityType.Fields;

        // By index: a foreach over the list of the schema would allocate its enumerator.
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];

            values[field.Ordinal] = reader.IsNull(field) ? null : Value(field, reader);
        }
    }

    // The value of a field that is not null, boxed in the exact type a record holds for it.
    private static object Value(LatticeField field, in RootReader reader)
    {
        switch (field.Declaration.ScalarType)
        {
            case LatticeFieldTypeEnum.Boolean:
                return reader.GetBoolean(field);
            case LatticeFieldTypeEnum.Int8:
                return (sbyte)reader.GetInteger(field);
            case LatticeFieldTypeEnum.Int16:
                return (short)reader.GetInteger(field);
            case LatticeFieldTypeEnum.Int32:
                return (int)reader.GetInteger(field);
            case LatticeFieldTypeEnum.Int64:
                return reader.GetInteger(field);
            case LatticeFieldTypeEnum.UInt8:
                return (byte)reader.GetUnsigned(field);
            case LatticeFieldTypeEnum.UInt16:
                return (ushort)reader.GetUnsigned(field);
            case LatticeFieldTypeEnum.UInt32:
                return (uint)reader.GetUnsigned(field);
            case LatticeFieldTypeEnum.UInt64:
                return reader.GetUnsigned(field);
            case LatticeFieldTypeEnum.Float32:
                return reader.GetFloat32(field);
            case LatticeFieldTypeEnum.Float64:
                return reader.GetFloat64(field);
            case LatticeFieldTypeEnum.Decimal:
                return reader.GetDecimal(field);
            case LatticeFieldTypeEnum.Guid:
                return reader.GetGuid(field);
            case LatticeFieldTypeEnum.DateTimeOffset:
                return reader.GetDateTimeOffset(field);
            case LatticeFieldTypeEnum.DateOnly:
                return reader.GetDateOnly(field);
            case LatticeFieldTypeEnum.TimeOnly:
                return reader.GetTimeOnly(field);
            case LatticeFieldTypeEnum.TimeSpan:
                return reader.GetTimeSpan(field);
            case LatticeFieldTypeEnum.String:
                return reader.GetString(field);
            case LatticeFieldTypeEnum.Binary:
                return reader.GetBinary(field);
            default:
                throw new UnreachableException($"The field {field.Name} is {field.Declaration.Type}, which the engine does not store.");
        }
    }
}
