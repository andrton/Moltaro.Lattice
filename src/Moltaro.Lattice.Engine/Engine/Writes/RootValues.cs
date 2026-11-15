using System.Diagnostics;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Writes;

// Sets the fields of a root from the values a write took of an object or a record: one value for
// each field, by LatticeField.Ordinal, in the form LatticeValueDeclaration.ValueType gives. The
// core took them and checked them against the schema (EntityBinding.Capture, LatticeRecord.Capture),
// so every value has the type its field needs and a field without a value is nullable; what the
// root writer checks beyond that, well-formed text and the bound of one value, it reports itself.
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

            switch (field.Declaration.Type)
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
                    // An Enum field, an owned value, a collection and a reference keep their entity
                    // type from being stored, so no write of one comes here.
                    throw new UnreachableException($"The field {entityType.Name}.{field.Name} is {field.Declaration.Type}, which the engine does not store.");
            }
        }
    }
}
