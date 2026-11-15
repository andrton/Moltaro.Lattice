using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Sets;

// The copy of one root as a record, for one read: a new record whose values the read puts into it
// in place, each in the form LatticeValueDeclaration.ValueType gives, a scalar boxed as a record
// holds it. Every value is made for this record alone, so the record owns what it holds.
internal struct RecordCopy : IRootCopy<LatticeRecord>
{
    private readonly LatticeEntityType _entityType;

    public RecordCopy(LatticeEntityType entityType)
    {
        _entityType = entityType;
    }

    public LatticeRecord? Result { get; private set; }

    public void Create() => Result = new LatticeRecord(_entityType);

    // The read holds the protection of the root.
    public readonly void Fill(in RootReader reader)
    {
        var values = Result!.Values;
        var fields = _entityType.Fields;

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
        switch (field.Declaration.Type)
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
