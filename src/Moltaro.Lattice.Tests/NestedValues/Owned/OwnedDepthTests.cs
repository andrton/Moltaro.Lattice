namespace Moltaro.Lattice.Tests.NestedValues.Owned;

// An owned value is stored with the entity that holds it, present or absent, at every depth the schema
// allows (LatticeFieldTypeEnum.Owned; LatticeValueDeclaration, "A declaration is at most 32 deep"). A
// nullable owned value may be absent, and then nothing beneath it has a value.
//
// The schema is a chain: the entity type Deep, whose key Code is a String, holds Top, a nullable owned value
// of Level1; Level1 to Level30 each hold an Int32 Value and Next, a nullable owned value of the level after
// it; Level31 holds only its Value. Level31 is 1 deep, as its Int32 is, an Owned of it 2, Level30 2, and so
// on: an Owned of Level1, the field Top, is 32 deep, the deepest a declaration may be. A chain present down
// to the level d, 0 for Top absent and 31 for every level present, holds the Value d at each level and
// stops below it.
public sealed class OwnedDepthTests
{
    private const int Levels = 31;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile(ChainJson());

    [Fact]
    public void The_chain_reaches_the_deepest_declaration_the_schema_allows()
    {
        Assert.True(Schema.TryGetEntityType("Deep", out var deep));
        Assert.True(deep.TryGetField("Top", out _));

        // One level more would be 33 deep, which the schema refuses.
        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(ChainJson(Levels + 1)));
        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Owned_value_is_read_back_present_down_to_every_depth_and_absent_below_it()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var deep = database.Set("Deep");

        for (var depth = 0; depth <= Levels; depth++)
        {
            await deep.InsertAsync(RecordOf(deep.EntityType, $"down to {depth}", depth));
        }

        for (var depth = 0; depth <= Levels; depth++)
        {
            Assert.Equal(depth, DepthOf(deep.Get($"down to {depth}")!, value: depth));
        }
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Absent_owned_value_replaced_by_a_present_one_and_back_is_read_back_each_time_at_every_depth()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var deep = database.Set("Deep");

        await deep.InsertAsync(RecordOf(deep.EntityType, "changing", 0));

        // Down to every depth and back to none, the deepest level appearing and disappearing in turn.
        foreach (var depth in new[] { Levels, 0, 1, 0, Levels, Levels - 1, Levels, 0 })
        {
            await deep.ReplaceAsync(RecordOf(deep.EntityType, "changing", depth));

            Assert.Equal(depth, DepthOf(deep.Get("changing")!, value: depth));
        }
    }

    // A record of Deep whose chain is present down to the level given, each level holding that number.
    private static LatticeRecord RecordOf(LatticeEntityType deep, string code, int depth)
    {
        LatticeOwnedValue? next = null;

        for (var level = depth; level >= 1; level--)
        {
            Assert.True(Schema.TryGetOwnedType($"Level{level}", out var ownedType));

            var value = new LatticeOwnedValue(ownedType) { ["Value"] = depth };

            if (level < Levels)
            {
                value["Next"] = next;
            }

            next = value;
        }

        return new LatticeRecord(deep) { ["Code"] = code, ["Top"] = next };
    }

    // How deep the chain of a record read back is present, after checking that every level holds the value
    // given and that the level below the last is absent.
    private static int DepthOf(LatticeRecord record, int value)
    {
        var depth = 0;
        var level = record.Get<LatticeOwnedValue>("Top");

        while (level is not null)
        {
            depth++;
            Assert.Equal($"Level{depth}", level.OwnedType.Name);
            Assert.Equal(value, level["Value"]);

            level = depth < Levels ? level.Get<LatticeOwnedValue>("Next") : null;
        }

        return depth;
    }

    // The chain of the levels given, 31 by default; 32 makes Top 33 deep.
    private static string ChainJson(int levels = Levels)
    {
        var ownedTypes = Enumerable.Range(1, levels).Select(level => level < levels
            ? $$"""{ "Id": {{level}}, "Name": "Level{{level}}", "Fields": [ { "Id": 1, "Name": "Value", "Type": "Int32" }, { "Id": 2, "Name": "Next", "Type": "Owned", "OwnedType": "Level{{level + 1}}", "Nullable": true } ] }"""
            : $$"""{ "Id": {{level}}, "Name": "Level{{level}}", "Fields": [ { "Id": 1, "Name": "Value", "Type": "Int32" } ] }""");

        return $$"""
            {
              "OwnedTypes": [ {{string.Join(", ", ownedTypes)}} ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Deep",
                  "Fields": [ { "Id": 1, "Name": "Code", "Type": "String" }, { "Id": 2, "Name": "Top", "Type": "Owned", "OwnedType": "Level1", "Nullable": true } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """;
    }
}
