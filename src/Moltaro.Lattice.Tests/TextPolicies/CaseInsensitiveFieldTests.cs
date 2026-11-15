namespace Moltaro.Lattice.Tests.TextPolicies;

// A String field compared without case is stored from Epic 1, sprint S07, and like every text it is
// read back exactly as it was written (Q04: the original text is kept in both comparisons): its case
// is not changed, and two values that the comparison holds equal stay two values, in a UTF-8 and in a
// UTF-16 database, through the class and by name. What the comparison decides, an index and a unique
// index, comes with S14 and S16.
//
// Engine only: the placeholder refuses an entity type with a comparison without case until S12.
public sealed class CaseInsensitiveFieldTests
{
    [Theory(Skip = NotImplemented.EngineStorage)]
    [InlineData(LatticeStringEncodingEnum.Utf8)]
    [InlineData(LatticeStringEncodingEnum.Utf16)]
    public async Task String_field_compared_without_case_is_written_and_read_back_as_it_was_given(LatticeStringEncodingEnum encoding)
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, SchemaOf(encoding));
        var handles = database.Set<Handle>();
        var records = database.Set("Handle");

        // Spellings that the comparison holds equal, in ASCII, outside ASCII and outside the basic
        // plane, in two entities each.
        Handle[] written =
        [
            new() { Id = 1, Text = "Main", Alias = null },
            new() { Id = 2, Text = "MAIN", Alias = "Été" },
            new() { Id = 3, Text = "𐐀𐐩", Alias = "éTÉ" },
            new() { Id = 4, Text = "𐐨𐐁", Alias = "" },
        ];

        await handles.InsertAsync(written[0]);
        await records.InsertAsync(RecordOf(records, written[1]));
        await handles.InsertAsync(written[2]);
        await records.InsertAsync(RecordOf(records, written[3]));

        ExpectReadBack(handles, records, written);

        // Replaced by other spellings, by name and through the class.
        written[0] = new() { Id = 1, Text = "mAiN", Alias = "ÉTÉ" };
        written[1] = new() { Id = 2, Text = "main", Alias = null };

        await records.ReplaceAsync(RecordOf(records, written[0]));
        await handles.UpsertAsync(written[1]);

        ExpectReadBack(handles, records, written);
    }

    private static void ExpectReadBack(ILatticeSet<Handle> handles, ILatticeSet records, Handle[] written)
    {
        foreach (var expected in written)
        {
            var handle = handles.Get(expected.Id);
            var record = records.Get(expected.Id);

            Assert.NotNull(handle);
            Assert.Equal(expected.Text, handle.Text);
            Assert.Equal(expected.Alias, handle.Alias);
            Assert.NotNull(record);
            Assert.Equal(expected.Text, record["Text"]);
            Assert.Equal(expected.Alias, record["Alias"]);
        }

        Assert.Equal(written.Length, handles.Enumerate().Count());
    }

    private static LatticeRecord RecordOf(ILatticeSet records, Handle handle) =>
        new(records.EntityType) { ["Id"] = handle.Id, ["Text"] = handle.Text, ["Alias"] = handle.Alias };

    // Handle has the key Id and two String fields compared without case, Text and the nullable Alias.
    private static LatticeSchema SchemaOf(LatticeStringEncodingEnum encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Handle", "ClrType": "{{typeof(Handle).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Text", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 3, "Name": "Alias", "Type": "String", "Comparison": "OrdinalIgnoreCase", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Handle
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";

        public string? Alias { get; set; }
    }
}
