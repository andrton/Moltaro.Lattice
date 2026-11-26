using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData(3_230_165, true)]
    [InlineData(3_230_166, true)]
    [InlineData(3_230_167, false)]
    public void A_List_of_19_byte_records_is_received_up_to_its_directory_count_bound(int count, bool fits)
    {
        const int DescriptorSize = 16;
        const int RecordStride = 19;
        const int RecordsPerChunk = 789;
        using var item = new RootArena(ResidentRecordLayoutTests.NarrowSchema);
        var inputHeader = new byte[sizeof(uint) + sizeof(long) + DescriptorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(inputHeader, item.Layout.LayoutVersion);
        MemoryMarshal.Write(inputHeader.AsSpan(sizeof(uint) + sizeof(long)), 5U);
        MemoryMarshal.Write(inputHeader.AsSpan(2 * sizeof(uint) + sizeof(long)), (uint)count);
        var first = new RepeatedPiece(inputHeader, 0);
        var end = first;
        var fullChunk = EmptyTextRecords(RecordsPerChunk);

        // Repeat the complete body even for the oversized count so insufficient input
        // cannot account for the refusal. Zero-length texts have a valid inline offset.
        for (var remaining = count; remaining > 0; remaining -= RecordsPerChunk)
        {
            end = end.Append(remaining >= RecordsPerChunk ? fullChunk : EmptyTextRecords(remaining));
        }

        var importer = new BlockImport(item.Arena);
        var input = new ReadOnlySequence<byte>(first, 0, end, end.Memory.Length);
        Assert.Equal(inputHeader.Length + (long)count * RecordStride, input.Length);
        var exception = Record.Exception(() =>
        {
            var reader = new SequenceReader<byte>(input);
            importer.Check(ref reader, item.Layout);
            Assert.True(reader.End);
        });

        if (fits)
        {
            Assert.Null(exception);
        }
        else
        {
            Assert.Equal(LatticeErrorEnum.ValidationFailed, Assert.IsType<LatticeException>(exception).Error);
        }

        Assert.Equal(0, item.Arena.Counters.PreparedBytes);

        static byte[] EmptyTextRecords(int records)
        {
            var chunk = new byte[records * RecordStride];
            for (var record = 0; record < records; record++)
            {
                var canonical = chunk.AsSpan(record * RecordStride + 3, DescriptorSize);
                MemoryMarshal.Write(canonical, 1U);
                MemoryMarshal.Write(canonical[8..], (ulong)(32 + chunk.Length));
            }

            return chunk;
        }
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
