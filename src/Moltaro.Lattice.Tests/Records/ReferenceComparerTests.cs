namespace Moltaro.Lattice.Tests.Records;

// A reference compares as the key of its target, with the comparison of that key. A Map key
// or a Set element that is a reference to an entity type whose key is a String compared with
// OrdinalIgnoreCase therefore ignores case too: keys that differ only in case are one key, and
// such a Set keeps the first. The layout fingerprint of the entity type that holds the
// collection never covers that comparison; the fingerprint of the schema does.
public sealed class ReferenceComparerTests
{
    [Fact]
    public void Set_of_references_compares_its_members_as_the_key_of_their_target()
    {
        var record = new LatticeRecord(Holder(Schema()));

        // Tag compares its key without case, Code with it.
        record["Labels"] = new[] { "Main", "main", "Other" };
        record["Codes"] = new[] { "Main", "main" };

        var labels = Assert.IsType<HashSet<string>>(record["Labels"]);
        Assert.Equal(2, labels.Count);
        Assert.True(labels.TryGetValue("MAIN", out var kept));
        Assert.Equal("Main", kept);
        Assert.Contains("other", labels);
        Assert.Equal(2, Assert.IsType<HashSet<string>>(record["Codes"]).Count);
    }

    [Fact]
    public void Map_of_references_refuses_two_keys_that_the_key_of_their_target_holds_equal()
    {
        var record = new LatticeRecord(Holder(Schema()));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["Main"] = 1, ["main"] = 2 };

        var exception = Assert.Throws<ArgumentException>(() => record["Counts"] = counts);
        Assert.Contains("Counts", exception.Message);

        record["Counts"] = new Dictionary<string, int> { ["Main"] = 1 };
        Assert.True(Assert.IsType<Dictionary<string, int>>(record["Counts"]).ContainsKey("MAIN"));
    }

    [Fact]
    public async Task Object_whose_Map_holds_two_keys_that_the_key_of_their_target_holds_equal_is_ValidationFailed()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema());
        var holder = new HolderObject { Id = 1, Counts = new(StringComparer.Ordinal) { ["Main"] = 1, ["main"] = 2 } };

        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => database.CreateWriteBatch().Insert(holder));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => database.Set<HolderObject>().InsertAsync(holder));
    }

    [Fact]
    public void Comparison_of_the_key_of_a_target_changes_the_schema_fingerprint_and_not_the_layout_fingerprint()
    {
        var ignoringCase = Schema();
        var withCase = LatticeSchema.Compile(Descriptor.With(Json, "'Type': 'String', 'Comparison': 'OrdinalIgnoreCase'", "'Type': 'String', 'Comparison': 'Ordinal'"));

        Assert.NotEqual(ignoringCase.Fingerprint, withCase.Fingerprint);
        Assert.Equal(Holder(ignoringCase).LayoutFingerprint, Holder(withCase).LayoutFingerprint);
    }

    private static LatticeSchema Schema() => LatticeSchema.Compile(Json);

    private static LatticeEntityType Holder(LatticeSchema schema)
    {
        Assert.True(schema.TryGetEntityType("Holder", out var holder));
        return holder;
    }

    private static string Json => $$"""
        {
          "EntityTypes": [
            { "Id": 1, "Name": "Tag", "Fields": [ { "Id": 1, "Name": "Name", "Type": "String", "Comparison": "OrdinalIgnoreCase" } ], "PrimaryKey": [1] },
            { "Id": 2, "Name": "Code", "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" } ], "PrimaryKey": [1] },
            {
              "Id": 3, "Name": "Holder", "ClrType": "{{typeof(HolderObject).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Labels", "Type": "Set", "Element": { "Type": "Reference", "EntityType": "Tag" } },
                { "Id": 3, "Name": "Counts", "Type": "Map", "Key": { "Type": "Reference", "EntityType": "Tag" }, "Value": { "Type": "Int32" } },
                { "Id": 4, "Name": "Codes", "Type": "Set", "Element": { "Type": "Reference", "EntityType": "Code" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    private sealed class HolderObject
    {
        public long Id { get; set; }

        public HashSet<string> Labels { get; set; } = [];

        public Dictionary<string, int> Counts { get; set; } = [];

        public HashSet<string> Codes { get; set; } = [];
    }
}
