using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// A late refusal must free a partly prepared tree, including its dictionary references.
// Later slots in an already copied root and record block have to be in the format the free
// walk reads even though placement did not reach them. See arena-protocol.md, "Stored structures
// and root transfer".
public sealed class ResidentDescriptorImportTests
{
    private const int TakenSegments = 5;
    private const int PrefixCount = 6;
    private const int PrefixBytes = 64;
    private const int RowCount = 7;
    private const int RecordStride = 83;

    private static readonly string KeptText = new('k', 89);
    private static readonly string NewText = new('n', 89);
    private static readonly string UnvisitedText = new('u', 89);
    private static readonly string Prefix = new('p', PrefixBytes);

    private static readonly LatticeSchema SeedSchema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Text', 'Type': 'String', 'StringStorage': 'Dictionary' }");

    private static readonly LatticeSchema Schema = SchemaOfImportedRoot();

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
        }, allocator, new MemoryBudget());

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
        var body = ExportBody(layout, rows);
        AssertUnvisitedRootDescriptor(layout, body);
        AssertUnvisitedRecordDescriptors(layout, body);

        var import = new BlockImport(arena);
        var input = new SequenceReader<byte>(new ReadOnlySequence<byte>(body));
        var received = import.Check(ref input, layout);
        Assert.True(input.End);
        var keys = new PrimaryKeys(type, layout.StringEncoding, seed: 0);
        Assert.Equal(2L, keys.ReadFixedKey(import.FixedKeyBytes(received)).Integer);
        Assert.Equal(before, arena.Counters);

        if (nativeFailure)
        {
            // The larger budget makes the refusal point visible inside the native
            // allocator, just before it could take one more segment.
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
            // and neither the later rows nor the root field after the list was visited.
            Assert.Equal(TakenSegments, arena.Counters.Segments);
            Assert.Equal(
                Arena.BlockSizeFor(RootPayloadLength(layout))
                + Arena.BlockSizeFor(RowCount * (RecordStride + 3))
                + Arena.BlockSizeFor(120)
                + Arena.BlockSizeFor(89), arena.Counters.PreparedBytes);
            Assert.Equal(new[] { (KeptText, 2L), (NewText, 1L) }, References(arena));
        }
    }

    private static LatticeSchema SchemaOfImportedRoot()
    {
        // All root slots share an unaligned skew. Select the skew so the
        // packed metadata would overlap the high byte of the inline offset.
        var halfWord = BitConverter.IsLittleEndian ? "{ 'Id': 3, 'Name': 'HalfWord', 'Type': 'Int16' }," : "";
        var prefixes = string.Join(", ", Enumerable.Range(10, PrefixCount).Select(id =>
            $"{{ 'Id': {id}, 'Name': 'Prefix{id}', 'Type': 'String' }}"));

        return ItemSchema.Compile($$"""
            { 'Id': 2, 'Name': 'Word', 'Type': 'Int32' },
            {{halfWord}}
            { 'Id': 4, 'Name': 'Byte', 'Type': 'Int8' },
            {{prefixes}},
            { 'Id': 20, 'Name': 'Entries', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Row' } },
            { 'Id': 21, 'Name': 'Tail', 'Type': 'String' }
            """,
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
    }

    private static byte[] ExportBody(RootLayout layout, LatticeOwnedValue[] rows)
    {
        var values = new List<object?> { 2L, 19 };
        if (BitConverter.IsLittleEndian)
        {
            values.Add((short)17);
        }

        values.Add((sbyte)3);
        values.AddRange(Enumerable.Repeat(Prefix, PrefixCount));
        values.Add(rows);
        values.Add("xyz");

        return RootValues.Export(layout, layout.EntityType, values.ToArray()).ToArray();
    }

    private static int RootPayloadLength(RootLayout layout) =>
        layout.FixedSize - BlockHeader.Size + PrefixCount * PrefixBytes + 3;

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

    private static void AssertUnvisitedRootDescriptor(RootLayout layout, byte[] body)
    {
        var entries = layout.StoredFieldOf(layout.EntityType.GetField("Entries")).Offset;
        var tail = layout.StoredFieldOf(layout.EntityType.GetField("Tail")).Offset;
        Assert.Equal(entries + 16, tail);
        Assert.Equal(BitConverter.IsLittleEndian ? 7 : 5, tail % sizeof(ulong));

        // Entries fails during its second record, before placement reaches Tail.
        var inline = body.AsSpan(sizeof(uint) + tail - BlockHeader.Size);
        AssertRollbackSentinel(inline, layout.FixedSize + PrefixCount * PrefixBytes);
    }

    private static void AssertUnvisitedRecordDescriptors(RootLayout layout, byte[] body)
    {
        var rows = layout.Structure.ElementOf(layout.EntityType.GetField("Entries").Ordinal);
        Assert.Equal(RecordStride, rows.Size);
        Assert.Equal(3, rows.DescriptorOffset);

        var unvisitedRecord = BitConverter.IsLittleEndian ? 4 : 6;
        var recordDescriptor = unvisitedRecord * rows.Size + rows.DescriptorOffset;
        Assert.Equal(BitConverter.IsLittleEndian ? 7 : 5, (BlockHeader.Size + recordDescriptor) % sizeof(ulong));

        var firstRecord = sizeof(uint) + RootPayloadLength(layout);
        var canonical = body.AsSpan(firstRecord + recordDescriptor);
        Assert.Equal(4U, MemoryMarshal.Read<uint>(canonical));
        Assert.Equal(2U, MemoryMarshal.Read<uint>(canonical[16..]));
        AssertRollbackSentinel(canonical[32..], BlockHeader.Size + RowCount * RecordStride + unvisitedRecord * 3);
        Assert.Equal(5U, MemoryMarshal.Read<uint>(canonical[48..]));
        Assert.Equal(0U, MemoryMarshal.Read<uint>(canonical[52..]));
        Assert.True(canonical.Slice(64, 16).SequenceEqual(new byte[16]));
    }

    private static void AssertRollbackSentinel(ReadOnlySpan<byte> canonical, int inlineOffset)
    {
        Assert.Equal(1U, MemoryMarshal.Read<uint>(canonical));
        Assert.Equal(3U, MemoryMarshal.Read<uint>(canonical[4..]));
        Assert.Equal((ulong)inlineOffset, MemoryMarshal.Read<ulong>(canonical[8..]));

        // At this skew the canonical offset's high byte gives the apparent
        // resident kind Block, while the apparent location names no arena segment.
        // Rollback could not free the prepared tree if it read this unconverted slot.
        var packedAt = BitConverter.IsLittleEndian ? 9 : 11;
        var locationAt = BitConverter.IsLittleEndian ? 1 : 3;
        Assert.Equal(2U, MemoryMarshal.Read<uint>(canonical[packedAt..]) & 0xF);
        Assert.True(MemoryMarshal.Read<ulong>(canonical[locationAt..]) >> 32 > TakenSegments);
    }
}
