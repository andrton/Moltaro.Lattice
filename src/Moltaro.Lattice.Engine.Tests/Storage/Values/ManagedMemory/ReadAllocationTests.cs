using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.ManagedMemory;

// A read allocates the value it returns and nothing else, as the coordinator's note on managed
// memory in the brief of S05 ("What the members do") states, and as the engine zone of AGENTS.md
// asks of a read path: no needless copy or allocation. The constructor of the reader, IsNull, GetLength and every
// member that returns a scalar allocate nothing. GetString and GetBinary allocate as much as a new
// string or array of the length they return, whether the value lay inline, in a block of its own or
// in chunks, and nothing for an empty value, which is the shared empty string or array. What a new
// string or array of a length takes is measured, not computed, so that the test does not depend on
// the object layout of the runtime. Each call is measured alone, after the same call warmed it up.
[Collection<ManagedAllocationCollection>]
public sealed class ReadAllocationTests
{
    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void The_reader_IsNull_GetLength_and_every_scalar_member_allocate_nothing(string encoding)
    {
        using var item = EveryType.Open(encoding);
        var values = new EveryValue(item);
        var root = values.Write();

        ReadEveryScalar(item, values.Fields, root);

        var start = GC.GetAllocatedBytesForCurrentThread();
        var reader = item.Reader(root);
        var constructed = GC.GetAllocatedBytesForCurrentThread() - start;

        ManagedAllocationAssert.Nothing("The constructor of the reader", constructed);

        foreach (var field in values.Fields)
        {
            start = GC.GetAllocatedBytesForCurrentThread();
            ReadScalar(reader, field);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            ManagedAllocationAssert.Nothing(field.Name, allocated);
        }
    }

    [Theory]
    [InlineData("Utf8", 0)]
    [InlineData("Utf8", 10)]
    [InlineData("Utf8", 1_000)]
    [InlineData("Utf8", 30_000)]
    [InlineData("Utf8", 200_000)]
    [InlineData("Utf16", 0)]
    [InlineData("Utf16", 10)]
    [InlineData("Utf16", 1_000)]
    [InlineData("Utf16", 30_000)]
    [InlineData("Utf16", 200_000)]
    public void GetString_allocates_the_string_it_returns_and_nothing_else(string encoding, int characters)
    {
        // Characters of three bytes in UTF-8, so that chunk boundaries fall inside them.
        var text = new string('€', characters);
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Value"), text);
        var root = writer.Complete();
        var field = item.Field("Value");
        var expected = characters == 0 ? 0 : Measure(() => new string(' ', characters));

        _ = item.Reader(root).GetString(field);

        var reader = item.Reader(root);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var read = reader.GetString(field);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(text, read);
        Assert.Equal(expected, allocated);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(70_000)]
    [InlineData(200_000)]
    public void GetBinary_allocates_the_array_it_returns_and_nothing_else(int length)
    {
        var value = new byte[length];
        value.AsSpan().Fill(0x5A);
        using var item = TypedItem.OfType("Binary");
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Value"), value);
        var root = writer.Complete();
        var field = item.Field("Value");
        var expected = length == 0 ? 0 : Measure(() => new byte[length]);

        _ = item.Reader(root).GetBinary(field);

        var reader = item.Reader(root);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var read = reader.GetBinary(field);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(value, read);
        Assert.Equal(expected, allocated);
    }

    // What the call allocates, measured after it ran once.
    private static long Measure(Func<object> call)
    {
        _ = call();

        var start = GC.GetAllocatedBytesForCurrentThread();
        _ = call();

        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static void ReadEveryScalar(RootArena item, LatticeField[] fields, ArenaAddress root)
    {
        var reader = item.Reader(root);

        foreach (var field in fields)
        {
            ReadScalar(reader, field);
        }
    }

    // IsNull of the field, and for a String or Binary field its length, for any other field its value
    // through the member that serves it.
    private static void ReadScalar(RootReader reader, LatticeField field)
    {
        _ = reader.IsNull(field);

        switch (field.Declaration.Type)
        {
            case LatticeFieldTypeEnum.Boolean:
                _ = reader.GetBoolean(field);
                break;
            case LatticeFieldTypeEnum.Int8 or LatticeFieldTypeEnum.Int16 or LatticeFieldTypeEnum.Int32 or LatticeFieldTypeEnum.Int64:
                _ = reader.GetInteger(field);
                break;
            case LatticeFieldTypeEnum.UInt8 or LatticeFieldTypeEnum.UInt16 or LatticeFieldTypeEnum.UInt32 or LatticeFieldTypeEnum.UInt64:
                _ = reader.GetUnsigned(field);
                break;
            case LatticeFieldTypeEnum.Enum when field.Declaration.EnumType!.UnderlyingType == LatticeFieldTypeEnum.Int16:
                _ = reader.GetInteger(field);
                break;
            case LatticeFieldTypeEnum.Enum:
                _ = reader.GetUnsigned(field);
                break;
            case LatticeFieldTypeEnum.Float32:
                _ = reader.GetFloat32(field);
                break;
            case LatticeFieldTypeEnum.Float64:
                _ = reader.GetFloat64(field);
                break;
            case LatticeFieldTypeEnum.Decimal:
                _ = reader.GetDecimal(field);
                break;
            case LatticeFieldTypeEnum.Guid:
                _ = reader.GetGuid(field);
                break;
            case LatticeFieldTypeEnum.DateTimeOffset:
                _ = reader.GetDateTimeOffset(field);
                break;
            case LatticeFieldTypeEnum.DateOnly:
                _ = reader.GetDateOnly(field);
                break;
            case LatticeFieldTypeEnum.TimeOnly:
                _ = reader.GetTimeOnly(field);
                break;
            case LatticeFieldTypeEnum.TimeSpan:
                _ = reader.GetTimeSpan(field);
                break;
            default:
                _ = reader.GetLength(field);
                break;
        }
    }
}
