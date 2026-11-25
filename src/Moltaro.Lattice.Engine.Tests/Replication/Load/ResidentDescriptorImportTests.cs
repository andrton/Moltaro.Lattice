using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// A late refusal must free a partly prepared tree, including its dictionary references.
// Later slots in an already copied record block have to be in the format the free walk
// reads even though placement did not reach them. See arena-protocol.md, "Stored structures and root transfer".
public sealed class ResidentDescriptorImportTests
{
    private const int TakenSegments = 4;
    private const int RowCount = 7;
    private const int RecordStride = 83;
    private static readonly string KeptText = new('k', 89);
    private static readonly string NewText = new('n', 89);
    private static readonly string UnvisitedText = new('u', 89);

    private static readonly LatticeSchema SeedSchema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Text', 'Type': 'String', 'StringStorage': 'Dictionary' }");

    private static readonly LatticeSchema Schema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Entries', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Row' } }",
        ownedTypes: """
            { 'Id': 1, 'Name': 'Row', 'Fields': [
              { 'Id': 1, 'Name': 'Code', 'Type': 'Int16' },
              { 'Id': 2, 'Name': 'Shared', 'Type': 'String', 'StringStorage': 'Dictionary' },
              { 'Id': 3, 'Name': 'Data', 'Type': 'Binary' },
              { 'Id': 4, 'Name': 'Inline', 'Type': 'String' },
              { 'Id': 5, 'Name': 'Empty', 'Type': 'List', 'Element': { 'Type': 'String' } },
              { 'Id': 6, 'Name': 'Optional', 'Type': 'String', 'Nullable': true }
            ] }
            """);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Late_import_failure_gives_back_its_tree_and_dictionary_references(bool nativeFailure)
    {
        var allocator = new CountingNativeAllocator();
        using var arena = new Arena(new ArenaOptions
        {
            SegmentSize = Arena.MaxBlockSize,
            MemoryLimit = (TakenSegments + (nativeFailure ? 1L : 0L)) * Arena.MaxBlockSize,
            ReservedSegments = 0,
        }, allocator);

        var seedType = Assert.Single(SeedSchema.EntityTypes);
        var seedLayout = RootLayout.Compile(seedType, SeedSchema);
        var seedWriter = new RootWriter(arena, seedLayout, new RootHandle(1, 1));
        RootValues.Write(ref seedWriter, seedType, [1L, KeptText]);
        var seed = RootArena.Publish(arena, seedLayout, seedWriter.Complete());
        var before = arena.Counters;

        Assert.Equal(2, before.Segments);
        Assert.Equal((KeptText, 1L), Assert.Single(References(arena)));

        var type = Assert.Single(Schema.EntityTypes);
        var layout = RootLayout.Compile(type, Schema);
        Assert.True(Schema.TryGetOwnedType("Row", out var row));
        LatticeOwnedValue[] rows =
        [
            Row(row, KeptText, 120),
            Row(row, NewText, 4_000),
            .. Enumerable.Range(2, RowCount - 2).Select(_ => Row(row, UnvisitedText, 95)),
        ];
        var body = RootValues.Export(layout, type, [2L, rows]).ToArray();
        AssertUnvisitedDescriptors(layout, body);

        var import = new BlockImport(arena);
        var input = new SequenceReader<byte>(new ReadOnlySequence<byte>(body));
        var received = import.Check(ref input, layout);
        Assert.True(input.End);
        Assert.Equal(2L, import.FixedPart(received).GetInteger(type.Key));
        Assert.Equal(before, arena.Counters);

        if (nativeFailure)
        {
            // The larger budget makes the refusal point visible inside the native
            // allocator, just before the fifth segment could be taken.
            allocator.FailAt = TakenSegments + 1;
            allocator.HoldNextAllocation(ObserveFailedRequest);
            Assert.Throws<OutOfMemoryException>(() => import.Place(received, new RootHandle(1, 2), out _));
            Assert.True(allocator.Failed);
        }
        else
        {
            Assert.True(import.Place(received, new RootHandle(1, 2), out var slotBytes).IsNull);
            Assert.Equal(0, slotBytes);
        }

        Assert.Equal(TakenSegments, allocator.Obtained);
        Assert.Equal(before, arena.Counters);
        Assert.Equal((KeptText, 1L), Assert.Single(References(arena)));
        Assert.Equal(KeptText, new RootReader(arena, seedLayout, seed).GetString(seedType.GetField("Text")));

        RootStorage.Free(arena, seedLayout, seed);
        Assert.Empty(References(arena));
        allocator.ExpectEverythingReturned();

        void ObserveFailedRequest()
        {
            if (allocator.Requests < TakenSegments)
            {
                allocator.HoldNextAllocation(ObserveFailedRequest);
                return;
            }

            // The list's holder and its first out-of-line Binary have both been
            // placed. The first row shares the original text, the next adds a text,
            // and the last row has not been visited.
            Assert.Equal(TakenSegments, arena.Counters.Segments);
            Assert.Equal(
                Arena.BlockSizeFor(layout.FixedSize - BlockHeader.Size)
                + Arena.BlockSizeFor(RowCount * (RecordStride + 3))
                + Arena.BlockSizeFor(120)
                + Arena.BlockSizeFor(89), arena.Counters.PreparedBytes);
            Assert.Equal(new[] { (KeptText, 2L), (NewText, 1L) }, References(arena));
        }
    }

    private static LatticeOwnedValue Row(LatticeOwnedType type, string text, int length) => new(type)
    {
        ["Code"] = (short)42,
        ["Shared"] = text,
        ["Data"] = new byte[length],
        ["Inline"] = "abc",
        ["Empty"] = Array.Empty<string>(),
        ["Optional"] = null,
    };

    private static (string Text, long References)[] References(Arena arena) =>
        [.. arena.Texts.References().Select(entry => (Encoding.UTF8.GetString(entry.Text), entry.References))];

    private static void AssertUnvisitedDescriptors(RootLayout layout, byte[] body)
    {
        var rows = layout.Structure.ElementOf(layout.EntityType.GetField("Entries").Ordinal);
        const int UnvisitedRecord = 4;
        Assert.Equal(RecordStride, rows.Size);
        Assert.Equal(3, rows.DescriptorOffset);

        var firstRecord = sizeof(uint) + layout.FixedSize - BlockHeader.Size;
        var recordDescriptor = UnvisitedRecord * rows.Size + rows.DescriptorOffset;
        var canonical = body.AsSpan(firstRecord + recordDescriptor);
        Assert.Equal(7, (BlockHeader.Size + recordDescriptor) % sizeof(ulong));
        Assert.Equal(4U, BinaryPrimitives.ReadUInt32LittleEndian(canonical));
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(canonical[16..]));
        var inline = canonical[32..];
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(inline));
        Assert.Equal(3U, BinaryPrimitives.ReadUInt32LittleEndian(inline[4..]));
        Assert.Equal((ulong)(BlockHeader.Size + (RowCount * RecordStride) + UnvisitedRecord * 3), BinaryPrimitives.ReadUInt64LittleEndian(inline[8..]));
        // At this skew an unconverted inline slot would be mistaken for an
        // out-of-line Block, with an invalid address, when rollback read it.
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(inline[9..]) & 0xF);
        Assert.Equal(5U, BinaryPrimitives.ReadUInt32LittleEndian(canonical[48..]));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(canonical[52..]));
        Assert.True(canonical.Slice(64, 16).SequenceEqual(new byte[16]));
    }
}
