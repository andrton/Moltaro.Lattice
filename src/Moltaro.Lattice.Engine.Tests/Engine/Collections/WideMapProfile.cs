using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// A Map can have no lookup and still store its entries in chunks. The key's
// domain bounds its count, and the value layout independently bounds its width.
internal sealed record WideMapProfile(string Name, LatticeFieldTypeEnum InnerKey, LatticeFieldTypeEnum ValueField, int FieldCount)
{
    public static IReadOnlyList<WideMapProfile> All { get; } =
    [
        new("PackedGuids", LatticeFieldTypeEnum.Int32, LatticeFieldTypeEnum.Guid, 600),
        new("BooleanRecords", LatticeFieldTypeEnum.Boolean, LatticeFieldTypeEnum.String, 420),
    ];

    public bool UsesRecords => ValueField == LatticeFieldTypeEnum.String;

    public LatticeSchema Schema(LatticeFieldTypeEnum primaryKey, string encoding)
    {
        var fields = string.Join(", ", Enumerable.Range(1, FieldCount).Select(id => $$"""
            { "Id": {{id}}, "Name": "Value{{id}}", "Type": "{{ValueField}}" }
            """));
        return LatticeSchema.Compile($$"""
            {
              "StringEncoding": "{{encoding}}",
              "OwnedTypes": [{ "Id": 1, "Name": "Wide", "Fields": [{{fields}}] }],
              "EntityTypes": [{
                "Id": 1, "Name": "Item",
                "Fields": [
                  { "Id": 1, "Name": "Id", "Type": "{{primaryKey}}" },
                  { "Id": 2, "Name": "Case", "Type": "Int32" },
                  { "Id": 3, "Name": "Values", "Type": "Map", "Nullable": true,
                    "Key": { "Type": "{{InnerKey}}" }, "Value": { "Type": "Owned", "OwnedType": "Wide" } },
                  { "Id": 4, "Name": "Stamp", "Type": "Int32" }
                ],
                "PrimaryKey": [1]
              }]
            }
            """);
    }

    public StructureLayout EntryLayout(LatticeSchema schema)
    {
        var type = Assert.Single(schema.EntityTypes);
        var row = RootLayout.Compile(type, schema).Structure.ElementOf(type.GetField("Values").Ordinal);
        Assert.Equal(UsesRecords, row.HoldsDescriptors);
        if (UsesRecords)
        {
            // 420 descriptors consume an 80-byte slot each with inline room.
            // The non-null Bool key is one more byte, leaving room for one record.
            Assert.Equal(16 * FieldCount + 1, row.Size);
            Assert.Equal(1, row.RecordsPerChunk);
        }
        else
        {
            // The width of the 600 Guid values and a fixed Int32 key.
            Assert.Equal(16 * FieldCount + sizeof(int), row.Size);
            Assert.InRange(row.Size, ValueBlocks.SingleBlockLimit + 1, ValueBlocks.ChunkPayload / 2);
        }

        return row;
    }

    public int[] Counts(StructureLayout row)
    {
        if (UsesRecords)
        {
            // The two distinct Boolean keys are the whole key domain.
            return [-1, row.RecordsPerChunk - 1, row.RecordsPerChunk, row.RecordsPerChunk + 1];
        }

        var beforeSplit = ValueBlocks.ChunkPayload / row.Size;
        return [.. new[] { -1, 0, 1, beforeSplit - 1, beforeSplit, beforeSplit + 1, 7, 8, 9 }.Distinct().Order()];
    }

    public string Shape(StructureLayout row, int count)
    {
        if (count < 0)
        {
            return "Null";
        }

        if (count == 0)
        {
            return UsesRecords ? "EmptyRecords" : "EmptyInline";
        }

        if (UsesRecords)
        {
            return count <= row.RecordsPerChunk ? "Records" : "RecordDirectory";
        }

        var entries = count * row.Size <= ValueBlocks.ChunkPayload
            ? "ByteDirectory/OneChunk"
            : "ByteDirectory/MultipleChunks";
        return count > 8 ? "Slots/" + entries : entries;
    }
}
