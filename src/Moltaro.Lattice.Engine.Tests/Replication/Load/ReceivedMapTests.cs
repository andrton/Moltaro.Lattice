using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// Check refuses bad Map descriptors, spans and counts before allocation. Its key is the first
// member of a List-shaped record; the layout puts the Int32 value before the String descriptor.
public sealed class ReceivedMapTests
{
    private static readonly LatticeSchema Schema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Entries', 'Type': 'Map', 'Key': { 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase' }, 'Value': { 'Type': 'Int32' } }");

    private const int MapDescriptor = sizeof(uint) + sizeof(long);

    private const int RecordStride = sizeof(int) + 16;

    private const int FirstKeyDescriptor = MapDescriptor + 16 + sizeof(int);

    [Fact(Skip = SkipReason.MapsAndSets)]
    public void A_hashed_Map_sends_its_records_and_their_inline_text_with_no_lookup_or_addresses()
    {
        const int Count = 9;
        const int TextLength = 2;
        const int RecordStart = MapDescriptor + 16;
        const int InlineStart = RecordStart + Count * RecordStride;
        using var item = new RootArena(Schema);
        var map = Enumerable.Range(0, Count).ToDictionary(index => $"k{index}", index => 100 + index);
        var expected = new byte[InlineStart + Count * TextLength];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, 2);
        BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(MapDescriptor), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(MapDescriptor + 4), Count);

        var ordinal = 0;
        foreach (var (key, value) in map)
        {
            var record = RecordStart + ordinal * RecordStride;
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(record), value);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(record + sizeof(int)), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(record + sizeof(int) + 4), TextLength);
            var inline = ordinal * TextLength;
            BinaryPrimitives.WriteUInt64LittleEndian(expected.AsSpan(record + sizeof(int) + 8), (ulong)(32 + Count * RecordStride + inline));
            expected[InlineStart + inline] = (byte)key[0];
            expected[InlineStart + inline + 1] = (byte)key[1];
            ordinal++;
        }

        var forwarded = RootValues.Export(item.Layout, item.EntityType, [1L, map]).ToArray();
        var input = new SequenceReader<byte>(new ReadOnlySequence<byte>(forwarded));
        var importer = new BlockImport(item.Arena);
        var received = importer.Check(ref input, item.Layout);
        var root = importer.Place(received, RootArena.Handle, out _);
        var emitted = new ArrayBufferWriter<byte>();
        BlockExport.Write(emitted, item.Arena, item.Layout, root);

        Assert.Equal(expected, forwarded);
        Assert.Equal(expected, emitted.WrittenSpan.ToArray());
        Assert.Equal(expected.Length, BlockExport.Length(item.Arena, item.Layout, root));
        RootStorage.Free(item.Arena, item.Layout, root);
    }

    [Theory(Skip = SkipReason.MapsAndSets)]
    [InlineData("entry count")]
    [InlineData("wrong kind")]
    [InlineData("Map address")]
    [InlineData("key address")]
    [InlineData("inline key span")]
    [InlineData("null key")]
    [InlineData("truncated payload")]
    [InlineData("extra payload")]
    public void A_malformed_Map_is_refused_before_any_arena_allocation(string damage)
    {
        using var item = new RootArena(Schema);
        var write = new Dictionary<string, int> { [new string('x', 70)] = 1, ["y"] = 2 };
        var bytes = RootValues.Export(item.Layout, item.EntityType, [1L, write]).ToArray();
        bytes = Damage(bytes, damage);
        var import = new BlockImport(item.Arena);
        var before = item.Arena.Counters;

        var error = Assert.Throws<LatticeException>(() =>
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
            import.Check(ref reader, item.Layout);
        });

        Assert.Equal(LatticeErrorEnum.ValidationFailed, error.Error);
        Assert.Equal(before, item.Arena.Counters);
    }

    [Fact(Skip = SkipReason.MapsAndSets)]
    public void A_received_Map_with_equal_keys_under_the_schema_comparer_is_refused_and_freed()
    {
        using var item = new RootArena(Schema);
        var write = new Dictionary<string, int> { ["A"] = 1, ["b"] = 2 };
        var bytes = RootValues.Export(item.Layout, item.EntityType, [1L, write]).ToArray();
        Assert.Equal((byte)'b', bytes[^1]);
        bytes[^1] = (byte)'a';
        var import = new BlockImport(item.Arena);

        var error = Assert.Throws<LatticeException>(() =>
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(bytes));
            var received = import.Check(ref reader, item.Layout);
            import.Place(received, RootArena.Handle, out _);
        });

        Assert.Equal(LatticeErrorEnum.ValidationFailed, error.Error);
        Assert.Contains("Entries", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, item.Arena.Counters.PreparedBytes);
        Assert.Equal(0, item.Arena.Counters.LiveBytes);
    }

    private static byte[] Damage(byte[] bytes, string damage)
    {
        switch (damage)
        {
            case "entry count":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(MapDescriptor + 4), uint.MaxValue);
                break;
            case "wrong kind":
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(MapDescriptor), (uint)DescriptorKindEnum.Block);
                break;
            case "Map address":
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(MapDescriptor + 8), 1);
                break;
            case "key address":
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(FirstKeyDescriptor + 8), 1);
                break;
            case "inline key span":
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(FirstKeyDescriptor + RecordStride + 8), 70_000);
                break;
            case "null key":
                bytes.AsSpan(FirstKeyDescriptor, 16).Clear();
                break;
            case "truncated payload":
                return bytes[..^1];
            case "extra payload":
                return [.. bytes, 0];
            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }

        return bytes;
    }
}
