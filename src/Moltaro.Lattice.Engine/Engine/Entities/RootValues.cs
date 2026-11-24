using System.Buffers;
using System.Collections;
using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Entities;

// Sets the fields of a root from the values a write took of an object or a record: one value for each
// field, by LatticeField.Ordinal, in the form LatticeValueDeclaration.ValueType gives. The core took
// them and checked them against the schema (EntityBinding.ValuesForWrite, LatticeRecord.ValuesForWrite),
// so every value has the type its field needs, a field without a value is nullable, its text is
// well-formed, its value within the bound of one value and the value of an Enum field one its enum type
// declares. The root writer checks the text and the bound again as the guard of the stored form, and
// reports itself what it refuses. An Enum field holds the integer of the underlying type of its enum
// type, and is written as that integer type, and a Reference holds the key of its target, written as the
// type of that key (LatticeValueDeclaration.ScalarType); an owned value is a LatticeOwnedValue and a List
// a List<T>, written and read at every depth by the writer and the reader of the root.
//
// The bound of a List is the engine's to check, since the core does not know the layout it follows from
// (StructureLayout.MostElements): CheckListBounds, on the thread of the caller, before a write is
// submitted and when a member is added to a write batch, so before the conditions of the write.
internal static class RootValues
{
    // Refuses, with ValidationFailed and its place named as the validation of the core names one, a List
    // among the values, at any depth, of more elements than one List of its elements holds: the bound of a
    // List, a fixed rule of the storage format as the bound of one String or Binary value is. The values
    // are those the core took and checked. The root writer refuses such a List again, as the guard of the
    // stored form (Storage.Roots.NestedValues).
    public static void CheckListBounds(RootLayout layout, object?[] values)
    {
        var entityType = layout.EntityType;

        foreach (var field in entityType.FieldsByOrdinal)
        {
            if (values[field.Ordinal] is { } value)
            {
                CheckListBounds(entityType, layout.Structure, entityType.Name, field.Ordinal, value);
            }
        }
    }

    // The writer is passed by reference: it is never copied (RootWriter).
    public static void Write(ref RootWriter writer, LatticeEntityType entityType, object?[] values)
    {
        foreach (var field in entityType.FieldsByOrdinal)
        {
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
                case LatticeFieldTypeEnum.Owned:
                    writer.SetOwned(field, (LatticeOwnedValue)value);
                    break;
                case LatticeFieldTypeEnum.List:
                    writer.SetList(field, value);
                    break;
                default:
                    // A Map and a Set keep their entity type from being stored, so no write of one comes here.
                    throw new UnreachableException($"The field {entityType.Name}.{field.Name} is {field.Declaration.Type}, which the engine does not store.");
            }
        }
    }

    // The root of the values in the load format, after its envelope, written without the arena as a child
    // writes the root of a write it forwards (Storage.Roots.RootWriter, "A root for a forwarded write"),
    // for a member of a write batch, which keeps it from its Add to its commit, when the write path places
    // it as it places a forwarded root (Writes.WriteExecutor). A root that fits one piece of a transfer is
    // one array of its exact length; a larger one is held in pieces (Replication.Load.TransferBuffer), so
    // that a root of any size is held and nothing of it lies in the large object heap. The last piece is
    // used in part: a root just above one piece takes two, up to twice its length, and a root of 3 MiB
    // takes 2 % more than its length. The root writer
    // refuses what it refuses for a write of values, and keeps nothing then.
    public static ReadOnlySequence<byte> Export(RootLayout layout, LatticeEntityType entityType, object?[] values)
    {
        var writer = new RootWriter(layout, ExportedValues.OfRoot(layout));

        try
        {
            Write(ref writer, entityType, values);

            var length = writer.ExportLength;

            if (length > TransferBuffer.PieceSize)
            {
                var pieces = new TransferBuffer();

                writer.Export(pieces);

                return pieces.Sequence;
            }

            var bytes = new ArrayBufferWriter<byte>((int)length);

            writer.Export(bytes);

            return new ReadOnlySequence<byte>(bytes.WrittenMemory);
        }
        catch
        {
            writer.Abandon();
            throw;
        }
    }

    // The Lists of the value of the place at the position of the structure, the place itself and those
    // beneath it, whose field the type named owner declares. A List whose element holds none is counted and
    // not entered.
    private static void CheckListBounds(LatticeEntityType entityType, StructureLayout layout, string owner, int position, object value)
    {
        ref readonly var place = ref layout.Place(position);

        if (place.Type == StoredTypeEnum.Owned)
        {
            var owned = (LatticeOwnedValue)value;

            foreach (var field in owned.OwnedType.FieldsByOrdinal)
            {
                if (owned.Values[field.Ordinal] is { } fieldValue)
                {
                    CheckListBounds(entityType, layout, owned.OwnedType.Name, layout.ChildOf(position, field), fieldValue);
                }
            }
        }
        else if (place.Type == StoredTypeEnum.List)
        {
            var element = layout.ElementOf(position);
            var list = (IList)value;

            if (list.Count > element.MostElements)
            {
                throw new LatticeException(
                    LatticeErrorEnum.ValidationFailed,
                    $"{entityType.Name}: field '{owner}.{place.Field.Name}' holds {list.Count:N0} elements, more than the {element.MostElements:N0} that one List of its elements may hold.");
            }

            // The one place of the record of an element is the List field itself, declared by the same owner.
            if (element.HoldsLists)
            {
                foreach (var item in list)
                {
                    if (item is not null)
                    {
                        CheckListBounds(entityType, element, owner, 0, item);
                    }
                }
            }
        }
    }

    // Reads every field of the root into the values, by LatticeField.Ordinal, in the form a write takes
    // and a record holds (LatticeValueDeclaration.ValueType): null for a field that is null, a scalar
    // boxed in its exact type — for an Enum field the integer of the underlying type of its enum type,
    // for a Reference the key of its target —, a new string or array for a String or Binary value, and a
    // new LatticeOwnedValue or List for an owned value or a List, made at every depth. The reverse of Write, for a read that copies
    // a root as a record (Sets.RecordCopy) and for a forwarded write a master writes again
    // (WriteExecutor).
    public static void Read(in RootReader reader, LatticeEntityType entityType, object?[] values)
    {
        foreach (var field in entityType.FieldsByOrdinal)
        {
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
            case LatticeFieldTypeEnum.Owned:
                return reader.GetOwned(field);
            case LatticeFieldTypeEnum.List:
                return reader.GetList(field);
            default:
                throw new UnreachableException($"The field {field.Name} is {field.Declaration.Type}, which the engine does not store.");
        }
    }
}
