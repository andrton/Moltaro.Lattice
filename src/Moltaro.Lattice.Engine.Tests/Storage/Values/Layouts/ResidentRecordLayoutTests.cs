using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// A record holding a descriptor remains packed, including at the last slot in the
// chunk. The holder layout follows arena-protocol.md, "Stored structures and root transfer".
public sealed class ResidentRecordLayoutTests
{
    internal static LatticeSchema NarrowSchema { get; } = SchemaFor(wide: false);

    [Theory(Skip = ResidentDescriptorTests.Pending)]
    [InlineData(false, 19, 789, 3_230_166, 62, 56)]
    [InlineData(true, 23, 752, 3_078_688, 70, 64)]
    public async Task Packed_records_keep_their_stride_and_wire_at_each_side_of_a_chunk_boundary(
        bool wide, int stride, int recordsPerChunk, int mostElements, int legacySecondLocation, int secondLocation)
    {
        var schema = wide ? SchemaFor(wide: true) : NarrowSchema;
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        var items = master.Set("Item");
        var rows = items.EntityType.GetField("Rows");
        var layout = RootLayout.Compile(items.EntityType, schema);
        var element = layout.Structure.ElementOf(rows.Ordinal);
        Assert.Equal(stride, element.Size);
        Assert.Equal(stride - 16, element.DescriptorOffset);
        Assert.Equal(recordsPerChunk, element.RecordsPerChunk);
        Assert.Equal(mostElements, element.MostElements);
        Assert.Equal(legacySecondLocation, BlockHeader.Size + stride + element.DescriptorOffset + sizeof(ulong));

        for (var count = recordsPerChunk - 1; count <= recordsPerChunk + 1; count++)
        {
            await items.InsertAsync(ItemOf(schema, items.EntityType, count, wide));
        }

        var seen = new HashSet<long>();
        master.ForEachLiveRoot((arena, stored, _, root) =>
        {
            var key = new RootReader(arena, stored, root).GetInteger(stored.EntityType.Key);
            Assert.True(seen.Add(key));

            var count = (int)key;
            var place = arena.Payload(root)[(stored.StoredFieldOf(rows).Offset - BlockHeader.Size)..];
            // Id occupies the first eight bytes, so this parent slot is aligned
            // and keeps its original byte layout. Test the record slots independently.
            var descriptor = new ValueDescriptor
            {
                Kind = (DescriptorKindEnum)BinaryPrimitives.ReadUInt32LittleEndian(place),
                Length = BinaryPrimitives.ReadUInt32LittleEndian(place[4..]),
                Location = BinaryPrimitives.ReadUInt64LittleEndian(place[8..]),
            };
            Assert.Equal(DescriptorKindEnum.Records, descriptor.Kind);
            Assert.Equal((uint)count, descriptor.Length);

            var chunks = new RecordBlocks(arena, descriptor, element);
            var chunkCount = 0;
            while (chunks.MoveNext())
            {
                var payload = arena.Payload(chunks.Block);
                var records = chunks.Count;
                var expectedInlineOffset = BlockHeader.Size + (records * stride);
                Assert.Equal(0, chunks.First % recordsPerChunk);

                if (records > 2)
                {
                    var inlineBytes = wide ? 3 : 4;
                    AssertInlineLocation(payload, secondLocation, expectedInlineOffset + inlineBytes);
                    AssertInlineLocation(payload, wide ? 88 : 80, expectedInlineOffset + 2 * inlineBytes);
                }

                AssertInlineLocation(payload, 40, expectedInlineOffset);
                chunkCount++;
            }

            Assert.Equal(count > recordsPerChunk ? 2 : 1, chunkCount);

            var expected = ExpectedWire(stored, ItemOf(schema, items.EntityType, count, wide));
            var output = new ArrayBufferWriter<byte>();
            BlockExport.Write(output, arena, stored, root);
            Assert.Equal(expected.Length, BlockExport.Length(arena, stored, root));
            Assert.Equal(expected, output.WrittenSpan.ToArray());
        });
        Assert.Equal(3, seen.Count);

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var childItems = child.Set("Item");
        for (var count = recordsPerChunk - 1; count <= recordsPerChunk + 1; count++)
        {
            AssertView(items, count, wide);
            AssertView(childItems, count, wide);
        }
    }

    private static LatticeSchema SchemaFor(bool wide) => ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Rows', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Row' } }",
        ownedTypes: wide
            ? """
              { 'Id': 1, 'Name': 'Row', 'Fields': [
                { 'Id': 1, 'Name': 'Rank', 'Type': 'Int32' },
                { 'Id': 2, 'Name': 'Flag', 'Type': 'Int16' },
                { 'Id': 3, 'Name': 'Code', 'Type': 'Int8' },
                { 'Id': 4, 'Name': 'Data', 'Type': 'Binary' }
              ] }
              """
            : """
              { 'Id': 1, 'Name': 'Row', 'Fields': [
                { 'Id': 1, 'Name': 'Rank', 'Type': 'Int16' },
                { 'Id': 2, 'Name': 'Code', 'Type': 'Int8' },
                { 'Id': 3, 'Name': 'Text', 'Type': 'String' }
              ] }
              """);

    private static LatticeRecord ItemOf(LatticeSchema schema, LatticeEntityType entityType, int count, bool wide)
    {
        Assert.True(schema.TryGetOwnedType("Row", out var rowType));
        return new LatticeRecord(entityType)
        {
            ["Id"] = (long)count,
            ["Rows"] = Enumerable.Range(0, count).Select(index => RowAt(rowType, index, wide)).ToArray(),
        };
    }

    private static LatticeOwnedValue RowAt(LatticeOwnedType rowType, int index, bool wide)
    {
        var row = new LatticeOwnedValue(rowType) { ["Code"] = (sbyte)(index % 83) };
        if (wide)
        {
            row["Rank"] = index;
            row["Flag"] = (short)(-index);
            row["Data"] = DataAt(index);
        }
        else
        {
            row["Rank"] = (short)index;
            row["Text"] = TextAt(index);
        }

        return row;
    }

    private static string TextAt(int index) => index.ToString("D4", CultureInfo.InvariantCulture);

    private static byte[] DataAt(int index) => [17, (byte)index, (byte)(index >> 8)];

    private static byte[] ExpectedWire(RootLayout layout, LatticeRecord item) =>
        RootValues.Export(layout, layout.EntityType, [item["Id"], item["Rows"]]).ToArray();

    private static void AssertInlineLocation(ReadOnlySpan<byte> payload, int locationAt, int expectedInlineOffset)
    {
        Assert.Equal(0, locationAt % sizeof(ulong));
        Assert.Equal((ulong)expectedInlineOffset, BinaryPrimitives.ReadUInt64LittleEndian(payload[(locationAt - BlockHeader.Size)..]));
    }

    private static void AssertView(ILatticeSet items, int count, bool wide)
    {
        Assert.True(items.TryView((long)count, (Count: count, Wide: wide), static (view, expected) =>
        {
            var rows = view["Rows"].List;
            Assert.Equal(expected.Count, rows.Count);
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index].Owned;
                Assert.Equal((sbyte)(index % 83), row["Code"].Get<sbyte>());
                if (expected.Wide)
                {
                    Assert.Equal(index, row["Rank"].Get<int>());
                    Assert.Equal((short)(-index), row["Flag"].Get<short>());
                    Assert.True(row["Data"].GetPart(0).SequenceEqual(DataAt(index)));
                }
                else
                {
                    Assert.Equal((short)index, row["Rank"].Get<short>());
                    Assert.Equal(TextAt(index), row["Text"].GetString());
                }
            }

            return rows.Count;
        }, out var observed));
        Assert.Equal(count, observed);
    }
}
