using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// The representative entry widths are chosen to cross each of the resident block and
// lookup boundaries. The data model and the expected shapes come from the schema and
// independent expected entries, with no reads of the database to build expectations.
internal abstract class CollectionMatrixProfile(string name, LatticeFieldTypeEnum kind, string declarations)
{
    public static IReadOnlyList<CollectionMatrixProfile> All { get; } =
    [
        new ListCollectionMatrixProfile<long?>("PackedList", """{ "Type": "Int64", "Nullable": true }""", Number),
        new ListCollectionMatrixProfile<string?>("RecordList", """{ "Type": "String", "Nullable": true }""", Text),
        new MapCollectionMatrixProfile<long, sbyte>("PackedMap", """{ "Type": "Int64" }""", """{ "Type": "Int8" }""", KeyNumber, Byte),
        new MapCollectionMatrixProfile<ulong, sbyte>("UnsignedMap", """{ "Type": "UInt64" }""", """{ "Type": "Int8" }""", UnsignedKeyNumber, Byte),
        new MapCollectionMatrixProfile<long, long>("WideMap", """{ "Type": "Int64" }""", """{ "Type": "Int64" }""", KeyNumber, KeyNumber),
        new MapCollectionMatrixProfile<string, string?>("RecordMap", """{ "Type": "String" }""", """{ "Type": "String", "Nullable": true }""", KeyText, Text),
        new SetCollectionMatrixProfile<long?>("PackedSet", """{ "Type": "Int64", "Nullable": true }""", Number),
        new SetCollectionMatrixProfile<Guid?>("WideSet", """{ "Type": "Guid", "Nullable": true }""", Identifier),
        new SetCollectionMatrixProfile<string?>("RecordSet", """{ "Type": "String", "Nullable": true }""", Text),
    ];

    public string Name { get; } = name;

    public LatticeFieldTypeEnum Kind { get; } = kind;

    public LatticeSchema Schema(LatticeFieldTypeEnum primaryKey, string encoding, string? clrType = null) =>
        LatticeSchema.Compile(SchemaJson(primaryKey, encoding, clrType));

    public string SchemaJson(LatticeFieldTypeEnum primaryKey, string encoding, string? clrType = null)
    {
        var binding = clrType is null ? "" : $"\"ClrType\": \"{clrType}\",";
        return $$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", {{binding}}
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "{{primaryKey}}" },
                { "Id": 2, "Name": "Case", "Type": "Int32" },
                { "Id": 3, "Name": "Values", "Type": "{{Kind}}", "Nullable": true, {{declarations}} },
                { "Id": 4, "Name": "Stamp", "Type": "Int32" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;
    }

    public int[] Counts(StructureLayout entries)
    {
        var counts = new HashSet<int> { -1, 0, 1 };
        if (entries.HoldsDescriptors)
        {
            Around(entries.RecordsPerChunk);
        }
        else
        {
            Around(ValueBlocks.InlineLimit / entries.Size);
            Around(ValueBlocks.SingleBlockLimit / entries.Size);
            Around(ValueBlocks.ChunkPayload / entries.Size);
        }

        if (Kind != LatticeFieldTypeEnum.List)
        {
            Around(CollectionLookup.MostCompactEntries);
            Around(LastContiguousLookup);
            Around(LastOneSlotChunk);
        }

        return [.. counts.Order()];

        void Around(int limit)
        {
            counts.Add(limit - 1);
            counts.Add(limit);
            counts.Add(limit + 1);
        }
    }

    // One derived slot uses eight bytes, and its maximum load is seven in ten.
    public static int LastContiguousLookup => (ValueBlocks.ChunkPayload - ValueBlocks.DescriptorSize) / sizeof(ulong) * 7 / 10;

    private static int LastOneSlotChunk => ValueBlocks.ChunkPayload / sizeof(ulong) * 7 / 10;

    public string ExpectedShape(StructureLayout entries, int count, bool retainedLookup = false)
    {
        if (count < 0)
        {
            return "Null";
        }

        if (count == 0)
        {
            return entries.HoldsDescriptors ? "EmptyRecords" : "EmptyInline";
        }

        var lookup = Kind != LatticeFieldTypeEnum.List && (count > CollectionLookup.MostCompactEntries || retainedLookup);
        var contents = ExpectedBytes(count * entries.Size, lookup);
        if (entries.HoldsDescriptors)
        {
            contents = count > entries.RecordsPerChunk ? "RecordDirectory" : "Records";
        }

        if (!lookup)
        {
            return contents;
        }

        if (count <= LastContiguousLookup)
        {
            return "Slots/" + contents;
        }

        return (count <= LastOneSlotChunk ? "SlotDirectory/OneChunk/" : "SlotDirectory/MultipleChunks/") + contents;
    }

    public static string ActualShape(Arena arena, in ValueDescriptor outer)
    {
        if (outer.Kind != DescriptorKindEnum.Lookup)
        {
            return ActualContents(arena, outer);
        }

        var lookup = ValueBlocks.AddressOf(outer);
        Assert.Equal(BlockKindEnum.Lookup, arena.Header(lookup).Kind);
        var payload = arena.Payload(lookup);
        var entries = ResidentDescriptor.ReadAligned(payload);
        var usesSlotDirectory = (payload.Length - ValueBlocks.DescriptorSize) / sizeof(ulong) < outer.Length;
        var slots = "Slots/";
        if (usesSlotDirectory)
        {
            var chunks = CollectionLookup.Chunks(arena, lookup);
            Assert.False(chunks.IsEmpty);
            foreach (var chunk in chunks)
            {
                Assert.Equal(BlockKindEnum.Chunk, arena.Header(chunk).Kind);
                Assert.Equal(lookup, arena.Header(chunk).Parent);
            }

            slots = chunks.Length == 1 ? "SlotDirectory/OneChunk/" : "SlotDirectory/MultipleChunks/";
        }

        return slots + ActualContents(arena, entries);
    }

    private static string ExpectedBytes(int bytes, bool lookup) => bytes switch
    {
        <= ValueBlocks.InlineLimit when !lookup => "Inline",
        <= ValueBlocks.SingleBlockLimit => "Block",
        <= ValueBlocks.ChunkPayload => "ByteDirectory/OneChunk",
        _ => "ByteDirectory/MultipleChunks",
    };

    private static string ActualContents(Arena arena, in ValueDescriptor descriptor)
    {
        if (descriptor.Kind == DescriptorKindEnum.Null)
        {
            Assert.Equal(default, descriptor);
            return "Null";
        }

        if (descriptor.Kind == DescriptorKindEnum.Inline)
        {
            return descriptor.Length == 0 ? "EmptyInline" : "Inline";
        }

        if (descriptor.Kind == DescriptorKindEnum.Records && descriptor.Length == 0)
        {
            Assert.Equal(0UL, descriptor.Location);
            return "EmptyRecords";
        }

        var address = ValueBlocks.AddressOf(descriptor);
        var blockKind = arena.Header(address).Kind;
        if (descriptor.Kind == DescriptorKindEnum.Records)
        {
            Assert.True(blockKind is BlockKindEnum.Value or BlockKindEnum.ChunkDirectory);
            return blockKind == BlockKindEnum.Value ? "Records" : "RecordDirectory";
        }

        if (descriptor.Kind == DescriptorKindEnum.Block)
        {
            Assert.Equal(BlockKindEnum.Value, blockKind);
            return "Block";
        }

        Assert.Equal(DescriptorKindEnum.Chunked, descriptor.Kind);
        Assert.Equal(BlockKindEnum.ChunkDirectory, blockKind);
        return arena.Payload(address).Length == ValueBlocks.DescriptorSize ? "ByteDirectory/OneChunk" : "ByteDirectory/MultipleChunks";
    }

    public abstract object? Values(int count, int salt);

    public abstract void AssertOwned(object? expected, LatticeRecord record);

    public abstract LatticeCommandResult[] Edit(LatticeUpdate update, int count, int salt);

    public virtual LatticeCommandResult Remove(LatticeUpdate update, int ordinal, int salt) =>
        throw new InvalidOperationException("This collection does not remove lookup members.");

    public abstract object? AfterEdit(int count, int oldSalt, int newSalt);

    public abstract void AssertView(LatticeValueView field, object? expected, ViewerPause? pause);

    private static byte Selector(int ordinal) => (byte)(ordinal % 81);

    private static sbyte Byte(int ordinal, int salt) => (sbyte)(Selector(ordinal) - salt);

    private static long KeyNumber(int ordinal, int salt) => 1_000_003L * salt + 31L * ordinal;

    private static ulong UnsignedKeyNumber(int ordinal, int salt) => (1UL << 63) + unchecked((ulong)KeyNumber(ordinal, salt));

    private static string KeyText(int ordinal, int salt) => string.Create(CultureInfo.InvariantCulture, $"ké€𐐨-{salt}-{ordinal:D6}");

    private static long? Number(int ordinal, int salt) => ordinal == 0 ? null : KeyNumber(ordinal, salt);

    private static Guid? Identifier(int ordinal, int salt) => ordinal == 0 ? null : new Guid(ordinal, (short)salt, 1, 2, 3, 4, 5, 6, 7, 8, 9);

    private static string? Text(int ordinal, int salt) => ordinal switch
    {
        0 => null,
        1 => "",
        _ => KeyText(ordinal, salt),
    };

    protected static T Read<T>(LatticeValueView value) => (T)TestScalarView.Read(value)!;

    protected static bool AssertNullField(LatticeValueView field, object? expected, ViewerPause? pause)
    {
        Assert.Equal(expected is null, field.IsNull);
        if (expected is not null)
        {
            return false;
        }

        pause?.Hold();
        Assert.True(field.IsNull);
        return true;
    }
}
