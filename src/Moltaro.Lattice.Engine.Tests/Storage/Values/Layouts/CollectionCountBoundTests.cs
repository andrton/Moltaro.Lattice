using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// The collection count is bounded by the entries and, for Map and Set, the derived lookup.
// The numbers come from the byte widths in the physical format, independent of its sizing code
// (arena-protocol.md, "Stored structures and root transfer"; LatticeFieldTypeEnum.Map and Set).
public sealed class CollectionCountBoundTests
{
    [Theory]
    [InlineData("'Type': 'Set', 'Element': { 'Type': 'Int64' }", 23_465_170)]
    [InlineData("'Type': 'Set', 'Element': { 'Type': 'Guid' }", 16_760_836)]
    [InlineData("'Type': 'Set', 'Element': { 'Type': 'String' }", 3_348_892)]
    [InlineData("'Type': 'Map', 'Key': { 'Type': 'Int64' }, 'Value': { 'Type': 'Int64' }", 16_760_836)]
    [InlineData("'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'String' }", 1_674_446)]
    public void The_collection_bound_is_the_smaller_of_the_entry_and_lookup_bounds(string declaration, int expected)
    {
        // 4,094 × 65,504 bytes of packed entries, or 4,094 record chunks with
        // floor(65,504 / (stride + 64 × descriptors)) entries each. A non-null String has a
        // 16-byte descriptor. The lookup holds at most floor(4,094 × (65,504 / 8) × 0.7) entries.
        var schema = ItemSchema.Compile($"{{ 'Id': 2, 'Name': 'Items', {declaration} }}");
        var entityType = Assert.Single(schema.EntityTypes);
        var layout = RootLayout.Compile(entityType, schema);
        Assert.True(entityType.TryGetField("Items", out var items));

        Assert.Equal(expected, layout.Structure.ElementOf(items.Ordinal).MostElements);
    }

    [Theory]
    [InlineData(23_465_170, true)]
    [InlineData(23_465_171, false)]
    public void Received_Set_entries_fit_up_to_the_lookup_count_bound(int count, bool fits)
    {
        using var item = RootArena.Of("{ 'Id': 2, 'Name': 'Items', 'Type': 'Set', 'Element': { 'Type': 'Int64' } }");
        var importer = new BlockImport(item.Arena);
        var wanted = count * sizeof(long);
        var head = new byte[sizeof(uint) + sizeof(long) + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(head, item.Layout.LayoutVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(sizeof(uint) + sizeof(long)), (uint)DescriptorKindEnum.Chunked);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(2 * sizeof(uint) + sizeof(long)), (uint)wanted);
        var first = new RepeatedPiece(head, 0);
        var end = first;
        var reusable = new byte[65_504];

        for (var left = wanted; left > 0; left -= reusable.Length)
        {
            end = end.Append(reusable.AsMemory(0, Math.Min(left, reusable.Length)));
        }

        // This checks the complete structural input size without allocating memory in proportion
        // to it. A placement would check uniqueness and is outside this structural count check.
        var input = new ReadOnlySequence<byte>(first, 0, end, end.Memory.Length);
        var failure = Record.Exception(() =>
        {
            var reader = new SequenceReader<byte>(input);
            importer.Check(ref reader, item.Layout);
            Assert.True(reader.End);
        });

        if (fits)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal(LatticeErrorEnum.ValidationFailed, Assert.IsType<LatticeException>(failure).Error);
        }

        Assert.Equal(0, item.Arena.Counters.PreparedBytes);
    }

    private sealed class RepeatedPiece : ReadOnlySequenceSegment<byte>
    {
        public RepeatedPiece(ReadOnlyMemory<byte> memory, long offset)
        {
            Memory = memory;
            RunningIndex = offset;
        }

        public RepeatedPiece Append(ReadOnlyMemory<byte> memory)
        {
            var next = new RepeatedPiece(memory, RunningIndex + Memory.Length);
            Next = next;

            return next;
        }
    }
}
