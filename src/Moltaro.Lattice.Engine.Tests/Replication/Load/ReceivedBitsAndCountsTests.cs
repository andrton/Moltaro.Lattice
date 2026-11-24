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

// What a child receives of a root with owned values and Lists is checked whole before anything is allocated
// for it, beyond the ways ReceivedTreeTests damages it (BlockImport.Check): a received root that a master could
// not have written is refused with ValidationFailed, without a byte of the arena taken and without managed
// memory in proportion to what the input announces:
// - a count within the bound of a List of records whose records take more bytes than the input has left,
//   compared in 64 bits before any record is read;
// - a field that is not nullable, null by its bit inside an owned value that is present;
// - the record of a packed List whose bits disagree: an element absent by its bit with the field beneath it
//   not null;
// - a packed List whose length is no whole number of its records.
//
// Item: Id, an Int64; Home, a nullable owned Address of City, a String, and Zip, an Int32; Tags, a List of
// String; Points, a List of nullable owned Points of Lat, a Float64. The bits of the root: Home 0, City 1,
// Zip 2. A record of Points is a byte of bits, the element 0 and Lat 1, and the 8 bytes of Lat: 9 bytes. The
// root written: Id 1, Home with the City Oslo and the Zip 150, Tags the texts a and b, Points one Point and
// one absent, 18 bytes inline in the root.
public sealed class ReceivedBitsAndCountsTests
{
    private const int Version = 4;

    private static readonly LatticeSchema Schema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address', 'Nullable': true }, { 'Id': 3, 'Name': 'Tags', 'Type': 'List', 'Element': { 'Type': 'String' } }, { 'Id': 4, 'Name': 'Points', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Point', 'Nullable': true } }",
        ownedTypes: "{ 'Id': 1, 'Name': 'Address', 'Fields': [ { 'Id': 1, 'Name': 'City', 'Type': 'String' }, { 'Id': 2, 'Name': 'Zip', 'Type': 'Int32' } ] }, { 'Id': 2, 'Name': 'Point', 'Fields': [ { 'Id': 1, 'Name': 'Lat', 'Type': 'Float64' } ] }");

    public static TheoryData<string> Damages =>
    [
        "a count within the bound of a List whose records take more bytes than are left",
        "a field that is not nullable null inside an owned value that is present",
        "a record of a packed List whose bits disagree",
        "a packed List whose length is no whole number of its records",
    ];

    [Fact]
    public void Root_received_with_bits_and_counts_a_master_writes_is_received_whole()
    {
        using var item = new RootArena(Schema);
        var import = new BlockImport(item.Arena);
        var reader = new SequenceReader<byte>(new ReadOnlySequence<byte>(InputOf(item)));
        var root = import.Place(import.Check(ref reader, item.Layout), RootArena.Handle, out _);

        Assert.False(root.IsNull);
        RootStorage.Free(item.Arena, item.Layout, root);
    }

    [Theory]
    [MemberData(nameof(Damages))]
    public void Root_received_with_bits_or_a_count_a_master_could_not_have_written_is_refused_before_anything_is_allocated(string damage)
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
        Assert.True(allocated < 64 * 1024, $"The check of {damage} allocated {allocated} bytes of managed memory.");
    }

    // The root of the test in the load format, after its envelope.
    private static byte[] InputOf(RootArena item)
    {
        Assert.True(Schema.TryGetOwnedType("Address", out var address));
        Assert.True(Schema.TryGetOwnedType("Point", out var point));

        var record = new LatticeRecord(item.EntityType)
        {
            ["Id"] = 1L,
            ["Home"] = new LatticeOwnedValue(address) { ["City"] = "Oslo", ["Zip"] = 150 },
            ["Tags"] = new[] { "a", "b" },
            ["Points"] = new[] { new LatticeOwnedValue(point) { ["Lat"] = 1.0 }, null },
        };
        var values = item.EntityType.Fields.Select(field => record[field.Name]).ToArray();

        return RootValues.Export(item.Layout, item.EntityType, values).ToArray();
    }

    // Where the descriptor of the field lies on the wire: after the layout version, at its offset in the root
    // block less the header.
    private static int DescriptorOf(RootArena item, string name)
    {
        Assert.True(item.EntityType.TryGetField(name, out var field));

        return Version + item.Layout.StoredFieldOf([field]).Offset - BlockHeader.Size;
    }

    private static byte[] Damaged(RootArena item, byte[] input, string damage)
    {
        var tags = DescriptorOf(item, "Tags");
        var points = DescriptorOf(item, "Points");

        switch (damage)
        {
            case "a count within the bound of a List whose records take more bytes than are left":
                var element = item.Layout.Structure.DescriptorElement((tags - Version + BlockHeader.Size - item.Layout.DescriptorOffset) / ValueBlocks.DescriptorSize)!;
                BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(tags + 4), (uint)element.MostElements);
                return input;

            case "a field that is not nullable null inside an owned value that is present":
                // The bit of Zip, the third of the root.
                input[Version] |= 1 << 2;
                return input;

            case "a record of a packed List whose bits disagree":
                // The second record of Points, absent: its element bit and the bit of Lat set; the bit of Lat
                // cleared, as if the absent element had a Lat.
                Assert.Equal((uint)DescriptorKindEnum.Inline, BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(points)));
                var second = Version + (int)BinaryPrimitives.ReadUInt64LittleEndian(input.AsSpan(points + 8)) - BlockHeader.Size + 9;
                Assert.Equal(0b11, input[second]);
                input[second] = 0b01;
                return input;

            default:
                BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(points + 4), 17);
                return input;
        }
    }
}
