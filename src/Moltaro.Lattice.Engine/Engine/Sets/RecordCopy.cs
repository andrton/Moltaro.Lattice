using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// The copy of one root as a record, for one read: a new record whose values the read puts into it
// in place, each in the form LatticeValueDeclaration.ValueType gives, a scalar boxed as a record
// holds it. Every value is made for this record alone, so the record owns what it holds.
//
// The record is the copy itself, made and filled under the protection of the root (IRootCopy.Copy),
// and Build only returns it: no code of the application runs there. LatticeRecord is a sealed class of
// the contract whose constructor and values array belong to the core, the values are boxed scalars,
// strings and arrays the engine makes, and nothing of a class of the application is reached.
internal struct RecordCopy : IRootCopy<LatticeRecord>
{
    private readonly LatticeEntityType _entityType;

    private LatticeRecord? _record;

    public RecordCopy(LatticeEntityType entityType)
    {
        _entityType = entityType;
    }

    public void Copy(in RootReader reader)
    {
        _record = new LatticeRecord(_entityType);

        var values = _record.Values;
        var fields = _entityType.Fields;

        // By index: a foreach over the list of the schema would allocate its enumerator.
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];

            values[field.Ordinal] = reader.IsNull(field) ? null : Value(field, reader);
        }
    }

    public readonly LatticeRecord Build() => _record!;

    // A record rents nothing.
    public readonly void Return()
    {
    }

    // The value of a field that is not null, boxed in the exact type a record holds for it: for an
    // Enum field the integer of the underlying type of its enum type (LatticeValueDeclaration.ScalarType).
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
