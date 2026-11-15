namespace Moltaro.Lattice.Tests.TextPolicies;

// The engine stores the entities of a schema of the UTF-16 encoding from Epic 1, sprint S07 (Q04.1):
// the encoding decides how text is kept, never what is read back, so an entity written to a UTF-16
// database reads back equal, through its class and by name. The texts are empty, short, of a block of
// their own and in chunks of the arena, outside ASCII and outside the basic plane, with a chunk
// boundary between the two halves of a surrogate pair; the key is a String outside the basic plane.
// Text that is not well-formed is refused there as in a UTF-8 database, by the validation of the core.
//
// Engine only: the placeholder refuses every write of a schema of the UTF-16 encoding until S12. The
// bound of one value in such a schema is stated by ValueBoundWriteTests.
public sealed class Utf16DatabaseTests
{
    // In UTF-16 a chunk holds 65,504 bytes, 32,752 code units: the code unit at that index is the
    // second half of 𐐨, the first lies before the boundary.
    private static readonly string Chunked = new string('x', 32_751) + "𐐨" + new string('y', 37_000);

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "StringEncoding": "Utf16",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Sample", "ClrType": "{{typeof(Sample).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Name", "Type": "String" },
                { "Id": 2, "Name": "Text", "Type": "String" },
                { "Id": 3, "Name": "Note", "Type": "String", "Nullable": true },
                { "Id": 4, "Name": "Count", "Type": "Int32" },
                { "Id": 5, "Name": "Data", "Type": "Binary", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact(Skip = NotImplemented.EngineStorage)]
    public async Task Entity_written_to_a_UTF16_database_reads_back_equal_through_its_class_and_by_name()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, Schema);
        var samples = database.Set<Sample>();
        var records = database.Set("Sample");

        Sample[] written =
        [
            new() { Name = "𐐀-1", Text = "", Note = null, Count = 1, Data = null },
            new() { Name = "Жук", Text = "Main é€𐐨", Note = string.Concat(Enumerable.Repeat("é€", 500)), Count = 2, Data = [1, 2, 3] },
            new() { Name = "😀", Text = Chunked, Note = "", Count = 3, Data = [] },
        ];

        await samples.InsertAsync(written[0]);
        await records.InsertAsync(RecordOf(records, written[1]));
        await samples.InsertAsync(written[2]);

        ExpectReadBack(samples, records, written);

        // Replaced by name and through the class, the texts exchanged.
        written[0] = new() { Name = "𐐀-1", Text = Chunked, Note = "Main é€𐐨", Count = 4, Data = [4] };
        written[2] = new() { Name = "😀", Text = "", Note = null, Count = 5, Data = null };

        await records.ReplaceAsync(RecordOf(records, written[0]));
        await samples.UpsertAsync(written[2]);

        ExpectReadBack(samples, records, written);
    }

    // Moved here from MalformedTextWriteTests, whose case on the engine stated that such text is refused
    // before the write is refused as not implemented: the engine stores a UTF-16 schema from this sprint,
    // so a write of well-formed text is stored.
    [Fact(Skip = NotImplemented.EngineStorage)]
    public async Task Text_that_is_not_well_formed_in_a_UTF16_database_is_ValidationFailed_and_well_formed_text_is_stored()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, Schema);
        var samples = database.Set<Sample>();
        var records = database.Set("Sample");
        var record = RecordOf(records, new Sample { Name = "a", Text = "a\uDE00b" });

        await TestData.ExpectValidationFailedAsync("Text", () => records.InsertAsync(record));
        await TestData.ExpectValidationFailedAsync("Text", () => records.UpsertAsync(record));
        await TestData.ExpectValidationFailedAsync("Text", () => samples.InsertAsync(new Sample { Name = "a", Text = "a\uDE00b" }));
        TestData.ExpectValidationFailed("Text", () => database.CreateWriteBatch().Insert(record));
        Assert.Empty(samples.Enumerate());

        record["Text"] = "well-formed";
        await records.InsertAsync(record);
        await samples.InsertAsync(new Sample { Name = "b", Text = "well-formed too" });

        Assert.Equal("well-formed", samples.Get("a")!.Text);
        Assert.Equal("well-formed too", records.Get("b")!["Text"]);
    }

    private static void ExpectReadBack(ILatticeSet<Sample> samples, ILatticeSet records, Sample[] written)
    {
        foreach (var expected in written)
        {
            var sample = samples.Get(expected.Name);
            var record = records.Get(expected.Name);

            Assert.NotNull(sample);
            Assert.Equal(expected.Name, sample.Name);
            Assert.Equal(expected.Text, sample.Text);
            Assert.Equal(expected.Note, sample.Note);
            Assert.Equal(expected.Count, sample.Count);
            Assert.Equal(expected.Data, sample.Data);

            Assert.NotNull(record);
            Assert.Equal(expected.Name, record["Name"]);
            Assert.Equal(expected.Text, record["Text"]);
            Assert.Equal(expected.Note, record["Note"]);
            Assert.Equal(expected.Count, record["Count"]);
            Assert.Equal(expected.Data, (byte[]?)record["Data"]);
        }

        Assert.Equal(written.Select(sample => sample.Name).Order(StringComparer.Ordinal), samples.Enumerate().Select(sample => sample.Name).Order(StringComparer.Ordinal));
    }

    private static LatticeRecord RecordOf(ILatticeSet records, Sample sample) => new(records.EntityType)
    {
        ["Name"] = sample.Name,
        ["Text"] = sample.Text,
        ["Note"] = sample.Note,
        ["Count"] = sample.Count,
        ["Data"] = sample.Data,
    };

    private sealed class Sample
    {
        public string Name { get; set; } = "";

        public string Text { get; set; } = "";

        public string? Note { get; set; }

        public int Count { get; set; }

        public byte[]? Data { get; set; }
    }
}
