using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.ManagedMemory;

// An entity of EveryType written with a value in every field, String and Binary values of every
// placement among them, for the tests that measure what a write and a read allocate on the managed
// heap. Everything a write needs is made before it is measured: the fields as an array, which is
// enumerated without an enumerator object, and the values. The writes call the members directly,
// because the delegates of WriterMembers would be allocated by the measured code.
internal sealed class EveryValue
{
    public EveryValue(RootArena item)
    {
        Item = item;
        Fields = [.. item.EntityType.Fields];
    }

    public RootArena Item { get; }

    public LatticeField[] Fields { get; }

    // A text inline and one chunked, in UTF-8 across a boundary inside a character, since 65,504 is
    // not a multiple of the three bytes of €; bytes in a block of their own and chunked.
    public string ShortText { get; } = "short text";

    public string LongText { get; } = new('€', 30_000);

    public byte[] MediumBytes { get; } = new byte[1_000];

    public byte[] LongBytes { get; } = new byte[70_000];

    // Writes the entity: the constructor of the writer, a Set… member for every field, Complete.
    public ArenaAddress Write()
    {
        var writer = Item.Writer();

        foreach (var field in Fields)
        {
            Set(ref writer, field);
        }

        return writer.Complete();
    }

    // Starts the same entity and abandons it after every field is set.
    public void WriteAndAbandon()
    {
        var writer = Item.Writer();

        foreach (var field in Fields)
        {
            Set(ref writer, field);
        }

        writer.Abandon();
    }

    private void Set(ref RootWriter writer, LatticeField field)
    {
        var declaration = field.Declaration;

        switch (declaration.Type)
        {
            case LatticeFieldTypeEnum.Boolean:
                writer.SetBoolean(field, true);
                break;
            case LatticeFieldTypeEnum.Int8 or LatticeFieldTypeEnum.Int16 or LatticeFieldTypeEnum.Int32 or LatticeFieldTypeEnum.Int64:
                writer.SetInteger(field, -1);
                break;
            case LatticeFieldTypeEnum.UInt8 or LatticeFieldTypeEnum.UInt16 or LatticeFieldTypeEnum.UInt32 or LatticeFieldTypeEnum.UInt64:
                writer.SetUnsigned(field, 1);
                break;
            case LatticeFieldTypeEnum.Enum when declaration.EnumType!.UnderlyingType == LatticeFieldTypeEnum.Int16:
                // Level, whose members are -5 and 7.
                writer.SetInteger(field, 7);
                break;
            case LatticeFieldTypeEnum.Enum:
                // Mask, a Flags type of UInt32 with the members 1 and 2.
                writer.SetUnsigned(field, 3);
                break;
            case LatticeFieldTypeEnum.Float32:
                writer.SetFloat32(field, 1.5f);
                break;
            case LatticeFieldTypeEnum.Float64:
                writer.SetFloat64(field, 2.5);
                break;
            case LatticeFieldTypeEnum.Decimal:
                writer.SetDecimal(field, 3.25m);
                break;
            case LatticeFieldTypeEnum.Guid:
                writer.SetGuid(field, Guid.AllBitsSet);
                break;
            case LatticeFieldTypeEnum.DateTimeOffset:
                writer.SetDateTimeOffset(field, DateTimeOffset.UnixEpoch);
                break;
            case LatticeFieldTypeEnum.DateOnly:
                writer.SetDateOnly(field, DateOnly.MaxValue);
                break;
            case LatticeFieldTypeEnum.TimeOnly:
                writer.SetTimeOnly(field, TimeOnly.MaxValue);
                break;
            case LatticeFieldTypeEnum.TimeSpan:
                writer.SetTimeSpan(field, TimeSpan.MinValue);
                break;
            case LatticeFieldTypeEnum.String:
                writer.SetString(field, declaration.IsNullable ? LongText : ShortText);
                break;
            case LatticeFieldTypeEnum.Binary:
                writer.SetBinary(field, declaration.IsNullable ? LongBytes : MediumBytes);
                break;
            default:
                throw new InvalidOperationException($"EveryType has no field of the type {declaration.Type}.");
        }
    }
}
