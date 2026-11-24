namespace Moltaro.Lattice.Tests.NestedValues.Bounds;

// Two rules of the schema language that the stored form of owned values and Lists brings, the same in every
// process, at their boundary. An owned value is stored inside its holder, so the fixed part of an entity, its
// owned values included, has to fit one block, 65,536 bytes with the header of 32; and the record of an
// element of a List has to fit one chunk of 65,504 bytes, a record that holds descriptors with room for a
// value of 64 bytes inline for each of them. A database whose schema breaks one does not open: the open
// fails with InvalidSchema and the entity type named, as it does for a fixed part beyond one block
// (LayoutsAtOpenTests). A schema at the boundary opens, and an entity of it is written and read back.
//
// The figures, from the layout: the key Code, a String, takes a descriptor of 16 bytes and a Decimal 16, a
// UInt8 one; a field beneath a nullable owned value has a bit of its own, as the owned value has.
// - Wide, an owned value that is not nullable, of 4,093 Decimals: 32 + 16 + 16 × 4,093 = 65,536 bytes. One
//   UInt8 more is 65,537.
// - Wide nullable, of 4,061 Decimals and three UInt8: 1 + 4,061 + 3 = 4,065 bits, a bitmap of 509 bytes, and
//   32 + 509 + 16 + 16 × 4,061 + 3 = 65,536. A fourth UInt8 is 4,066 bits, still 509 bytes, and 65,537.
// - Rows, a List of an owned value of k Decimals and one String: a record of 16k + 16 bytes with one
//   descriptor, so 16k + 16 + 64 has to be at most 65,504, and k = 4,089 is the most, 65,504; 4,090 is above.
// - Cells, a List of an owned value of k Decimals: a record of 16k bytes with no descriptor, at most 65,504,
//   so k = 4,094 is the most; 4,095 is above.
public sealed class SchemaRuleTests
{
    public static TheoryData<string, int, int, bool> FixedParts => new()
    {
        { "Wide", 4_093, 0, false },
        { "WideNullable", 4_061, 3, true },
    };

    public static TheoryData<string, int, bool> Records => new()
    {
        { "Rows", 4_089, true },
        { "Cells", 4_094, false },
    };

    [Theory]
    [MemberData(nameof(FixedParts))]
    public async Task Entity_whose_fixed_part_with_its_owned_values_fills_one_block_opens_and_one_byte_more_does_not(string name, int decimals, int bytes, bool nullable)
    {
        var fitting = OwnedFieldSchema(name, decimals, bytes, nullable);

        await using (var database = await TestDatabases.OpenMasterAsync(fitting))
        {
            var holders = database.Set(name);
            var record = new LatticeRecord(holders.EntityType) { ["Code"] = "one", ["Body"] = ValueOf(holders.EntityType, "Body") };

            await holders.InsertAsync(record);

            var body = holders.Get("one")!.Get<LatticeOwnedValue>("Body")!;
            Assert.All(body.OwnedType.Fields, field => Assert.Equal(record.Get<LatticeOwnedValue>("Body")![field.Name], body[field.Name]));
        }

        await ExpectNotOpenedAsync(OwnedFieldSchema(name, decimals, bytes + 1, nullable), name);
    }

    [Theory]
    [MemberData(nameof(Records))]
    public async Task List_whose_element_record_fills_one_chunk_opens_and_one_field_more_does_not(string name, int decimals, bool withText)
    {
        var fitting = ListSchema(name, decimals, withText);

        await using (var database = await TestDatabases.OpenMasterAsync(fitting))
        {
            var holders = database.Set(name);
            var element = ValueOf(holders.EntityType, "Items");
            var record = new LatticeRecord(holders.EntityType) { ["Code"] = "one", ["Items"] = new[] { element, element } };

            await holders.InsertAsync(record);

            var items = holders.Get("one")!.Get<List<LatticeOwnedValue>>("Items")!;
            Assert.Equal(2, items.Count);
            Assert.All(items, item => Assert.All(item.OwnedType.Fields, field => Assert.Equal(element[field.Name], item[field.Name])));
        }

        await ExpectNotOpenedAsync(ListSchema(name, decimals + 1, withText), name);
    }

    private static async Task ExpectNotOpenedAsync(LatticeSchema schema, string entityType)
    {
        var database = TestDatabases.Create(new LatticeDatabaseOptions { PersistenceMode = LatticePersistenceModeEnum.MemoryOnly, Schema = schema });

        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await database.OpenAsync());

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains(entityType, exception.Message, StringComparison.Ordinal);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
    }

    // An owned value of the owned type of the field, every Decimal its identity and every UInt8 its
    // identity less the number of Decimals, every String its name.
    private static LatticeOwnedValue ValueOf(LatticeEntityType entityType, string fieldName)
    {
        Assert.True(entityType.TryGetField(fieldName, out var field));

        var ownedType = (field.Declaration.OwnedType ?? field.Declaration.Element!.OwnedType)!;
        var value = new LatticeOwnedValue(ownedType);

        foreach (var member in ownedType.Fields)
        {
            value[member.Name] = member.Declaration.Type switch
            {
                LatticeFieldTypeEnum.Decimal => (decimal)member.Id / 7,
                LatticeFieldTypeEnum.UInt8 => (byte)member.Id,
                _ => member.Name,
            };
        }

        return value;
    }

    // An entity type of the name given, the key Code and Body, an owned value of that many Decimals and UInt8.
    private static LatticeSchema OwnedFieldSchema(string name, int decimals, int bytes, bool nullable) => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [ { "Id": 1, "Name": "Body", "Fields": [ {{Fields(decimals, bytes, withText: false)}} ] } ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "{{name}}",
              "Fields": [ { "Id": 1, "Name": "Code", "Type": "String" }, { "Id": 2, "Name": "Body", "Type": "Owned", "OwnedType": "Body", "Nullable": {{(nullable ? "true" : "false")}} } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // An entity type of the name given, the key Code and Items, a List of an owned value of that many Decimals
    // and, when withText is true, one String.
    private static LatticeSchema ListSchema(string name, int decimals, bool withText) => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [ { "Id": 1, "Name": "Item", "Fields": [ {{Fields(decimals, 0, withText)}} ] } ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "{{name}}",
              "Fields": [ { "Id": 1, "Name": "Code", "Type": "String" }, { "Id": 2, "Name": "Items", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Item" } } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private static string Fields(int decimals, int bytes, bool withText)
    {
        var fields = Enumerable.Range(1, decimals).Select(id => $$"""{ "Id": {{id}}, "Name": "Decimal{{id}}", "Type": "Decimal" }""")
            .Concat(Enumerable.Range(decimals + 1, bytes).Select(id => $$"""{ "Id": {{id}}, "Name": "Byte{{id}}", "Type": "UInt8" }"""));

        if (withText)
        {
            fields = fields.Append($$"""{ "Id": {{decimals + bytes + 1}}, "Name": "Text", "Type": "String" }""");
        }

        return string.Join(", ", fields);
    }
}
