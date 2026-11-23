using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// What a child receives of a root with owned values and Lists is checked whole, by the layout, before anything
// is allocated for it (BlockImport.Check; docs/design/milestone-1-engine-contract.md, "Native layout and
// allocation": invalid addresses, counts and lengths are rejected on import before any dereference). The wire
// carries the layout version, the root payload with its locations zero, and then the values out of line in
// preorder, a List of records as the payload of the block of its records followed by the values out of line
// of its records, with no offset between values (RootValues.Export writes it as a child forwards a write). Each
// way the input can be wrong is refused with ValidationFailed, without a byte of the arena taken and without
// managed memory in proportion to what the input announces:
// - a count that overflows: the count of a List of records of uint.MaxValue, whose records would take far more
//   bytes than the input has, compared in 64 bits;
// - a kind that does not fit its count: that List given the kind of a List of packed bytes;
// - null bits that disagree: the presence bit of Home set, absent, while the fields beneath it have values and
//   their bits clear;
// - a location that is not zero: the descriptor of a value out of line inside a record naming an address;
// - an inline span outside its block: a value inline in the block of the records lying past its end;
// - a total that is not exact: one byte more, and one byte less, than the root and its values describe.
//
// Item: Id, an Int64; Home, a nullable owned Address of City, a String, and Zip, an Int32; Tags, a List of
// String; Scores, a List of Int64. The bits: Home 0, City 1, Zip 2: one byte. The fixed fields from 33: Id 8,
// Home.Zip 4, to 45; the descriptors of Home.City, Tags and Scores, to 93. The root written: Id 1, Home with
// the City Oslo, 4 bytes inline in the root, and the Zip 150; Tags a text of 100 bytes, out of line in a block of
// its own, and the text y, inline in the block of the records; Scores 1, 2 and 3, 24 bytes inline in the root.
// So the root payload is 93 - 32 + 4 + 24 = 89 bytes after the 4 of the layout version, the block of the
// records of Tags 2 × 16 + 1 = 33 bytes after it, and the text of 100 bytes last: 226 bytes in all.
public sealed class ReceivedTreeTests
{
    private const int Version = 4;

    private const int RootPayload = 89;

    private const int RecordsBlock = 33;

    private const int LongTag = 100;

    // Where the records of Tags begin on the wire, each 16 bytes: the descriptor of its one String.
    private const int FirstRecord = Version + RootPayload;

    private static readonly LatticeSchema Schema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address', 'Nullable': true }, { 'Id': 3, 'Name': 'Tags', 'Type': 'List', 'Element': { 'Type': 'String' } }, { 'Id': 4, 'Name': 'Scores', 'Type': 'List', 'Element': { 'Type': 'Int64' } }",
        ownedTypes: "{ 'Id': 1, 'Name': 'Address', 'Fields': [ { 'Id': 1, 'Name': 'City', 'Type': 'String' }, { 'Id': 2, 'Name': 'Zip', 'Type': 'Int32' } ] }");

    public static TheoryData<string> Damages =>
    [
        "a count that overflows",
        "a kind that does not fit its count",
        "null bits that disagree",
        "a location that is not zero",
        "an inline span outside its block",
        "one byte more than the root describes",
        "one byte less than the root describes",
    ];

    [Fact(Skip = SkipReason.NestedValues)]
    public void The_wire_carries_the_root_its_records_and_its_values_out_of_line_in_preorder_and_is_received_whole()
    {
        using var item = new RootArena(Schema);
        var input = InputOf(item);

        Assert.Equal(Version + RootPayload + RecordsBlock + LongTag, input.Length);
        Assert.Equal(item.Layout.LayoutVersion, BinaryPrimitives.ReadUInt32LittleEndian(input));

        // The descriptor of Tags in the root: a List of records, its count 2, its location 0.
        var tags = TagsDescriptor(item);
        Assert.Equal((uint)DescriptorKindEnum.Records, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(tags)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(tags + 4)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(tags + 8)));

        // The first record names the text of 100 bytes in a block, at location 0; the text ends the input.
        Assert.Equal((uint)DescriptorKindEnum.Block, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(FirstRecord)));
        Assert.Equal((uint)LongTag, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(FirstRecord + 4)));
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(FirstRecord + 8)));
        Assert.All(input[^LongTag..], value => Assert.Equal((byte)'t', value));

        var import = new BlockImport(item.Arena);
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(input));
        var received = import.Check(ref reader, item.Layout);
        var root = import.Place(received, RootArena.Handle, out _);

        Assert.False(root.IsNull);
        RootStorage.Free(item.Arena, item.Layout, root);
    }

    [Theory(Skip = SkipReason.NestedValues)]
    [MemberData(nameof(Damages))]
    public void Root_received_with_a_tree_that_is_not_valid_is_refused_before_anything_is_allocated(string damage)
    {
        using var item = new RootArena(Schema);
        var input = Damaged(item, InputOf(item), damage);
        var import = new BlockImport(item.Arena);
        var before = item.Arena.Counters;
        var managed = GC.GetAllocatedBytesForCurrentThread();

        var exception = Assert.Throws<LatticeException>(() =>
        {
            var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(input));

            import.Check(ref reader, item.Layout);
        });

        var allocated = GC.GetAllocatedBytesForCurrentThread() - managed;

        Assert.Equal(LatticeErrorEnum.ValidationFailed, exception.Error);
        Assert.Equal(before, item.Arena.Counters);

        // The refusal and its message, and nothing that grows with the count or the length announced.
        Assert.True(allocated < 64 * 1024, $"The check of {damage} allocated {allocated} bytes of managed memory.");
    }

    // The root of the test in the load format, after its envelope.
    private static byte[] InputOf(RootArena item)
    {
        Assert.True(Schema.TryGetOwnedType("Address", out var address));

        var record = new LatticeRecord(item.EntityType)
        {
            ["Id"] = 1L,
            ["Home"] = new LatticeOwnedValue(address) { ["City"] = "Oslo", ["Zip"] = 150 },
            ["Tags"] = new[] { new string('t', LongTag), "y" },
            ["Scores"] = new long[] { 1, 2, 3 },
        };
        var values = item.EntityType.Fields.Select(field => record[field.Name]).ToArray();

        return RootValues.Export(item.Layout, item.EntityType, values).ToArray();
    }

    // Where the descriptor of Tags lies on the wire: after the layout version, at its offset in the root block
    // less the header.
    private static int TagsDescriptor(RootArena item)
    {
        Assert.True(item.EntityType.TryGetField("Tags", out var tags));

        return Version + item.Layout.StoredFieldOf([tags]).Offset - BlockHeader.Size;
    }

    private static byte[] Damaged(RootArena item, byte[] input, string damage)
    {
        var tags = TagsDescriptor(item);
        var secondRecord = FirstRecord + ValueBlocks.DescriptorSize;

        switch (damage)
        {
            case "a count that overflows":
                BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(tags + 4), uint.MaxValue);
                return input;

            case "a kind that does not fit its count":
                BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(tags), (uint)DescriptorKindEnum.Block);
                return input;

            case "null bits that disagree":
                input[Version] |= 1;
                return input;

            case "a location that is not zero":
                BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(FirstRecord + 8), 1);
                return input;

            case "an inline span outside its block":
                // The text y lies at the end of the block of the records, from 32 + 32 with its header; one byte
                // further it ends past the block.
                Assert.Equal((uint)DescriptorKindEnum.Inline, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(secondRecord)));
                BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(secondRecord + 8), (ulong)(BlockHeader.Size + RecordsBlock));
                return input;

            case "one byte more than the root describes":
                return [.. input, 0];

            default:
                return input[..^1];
        }
    }
}
