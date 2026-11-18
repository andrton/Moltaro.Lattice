using Moltaro.Lattice.TestSupport.Values;

namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// One String or Binary value takes at most 268,173,376 bytes of its stored encoding, for text the
// encoding the schema declares in StringEncoding (the remarks of String and Binary of
// LatticeFieldTypeEnum). The bound is a validation of the contract (brief of Epic 1, sprint S06, the
// coordinator's note after the second review; the remarks of ILatticeSet<TEntity> and
// ILatticeWriteBatch): a write of an entity with a larger value, and adding one to a write batch, is
// ValidationFailed with the field named, through the class and by name, before the conditions of the
// write are looked at. A value of exactly the bound passes the validation.
//
// The bound is one check of the validation of the core, which the capture of an object and the
// submission of a record make first. So these tests state it once for each way in, with values of
// the real size: through the class and by name, a write that would otherwise succeed and one that
// would otherwise fail on a condition, and an operation added to a batch, all with text; a Binary
// value once, judged by its length; and a value of exactly the bound once. That the validation of an entity comes before the conditions of every form of a write
// and before a write is refused as not implemented, also for an entity type that is not stored yet
// and in a schema of the UTF-16 encoding, and that adding to a batch validates in the same way, is
// stated by MalformedTextWriteTests for text that is not well-formed, which the same capture of an
// object and submission of a record refuse.
//
// The values are large, so the tests hold few of them and each only for the write that needs it:
// the text of ValueBound, 179 MB, 268,173,377 bytes in UTF-8, built once in the test process; that
// text without its last character, exactly the bound, made inside its write; and an array of
// 268,173,377 bytes, made inside its write and refused for its length before anything reads or
// copies it, so it is not initialized and its memory is never touched. The class runs alone
// (LargeValueCollection), so that what it holds is not added to what other tests hold.
//
// In a schema of the UTF-16 encoding, where a code unit takes two bytes, the bound is 134,086,688
// characters: that many pass the validation and one more is ValidationFailed (brief of Epic 1, sprint
// S07, where such a schema is first written). Each text is made inside its write, which ends at the
// validation or at its condition, so the engine never copies it; the one above the bound is refused
// for its length and its memory is never touched, so the test holds one text of 268 MB.
[Collection<LargeValueCollection>]
public sealed class ValueBoundWriteTests
{
    private static readonly LatticeSchema Utf16Schema = LatticeSchema.Compile($$"""
        {
          "StringEncoding": "Utf16",
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Caption", "Type": "String", "Nullable": true } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Caption", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "Attachment", "Type": "Binary", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact]
    public async Task Write_of_a_value_above_the_bound_of_one_value_is_ValidationFailed_with_the_field_named_before_the_conditions_of_the_write()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var notes = database.Set<Note>();
        var noteRecords = database.Set("Note");
        await notes.InsertAsync(new Note { Id = 1, Caption = "kept" });
        Assert.True(notes.TryGet(1L, out _, out var revision));
        var text = ValueBound.TextOneByteAboveInUtf8;

        // Text, through the class: an upsert of an absent key, which would otherwise succeed, and a
        // replace of an absent key, which would otherwise be NotFound.
        await TestData.ExpectValidationFailedAsync("Caption", () => notes.UpsertAsync(new Note { Id = 2, Caption = text }));
        await TestData.ExpectValidationFailedAsync("Caption", () => notes.ReplaceAsync(new Note { Id = 404, Caption = text }));

        // By name: an insert of an absent key, and an insert of the key of the stored entity, which
        // would otherwise be DuplicateKey.
        await TestData.ExpectValidationFailedAsync("Caption", () => noteRecords.InsertAsync(new LatticeRecord(noteRecords.EntityType) { ["Id"] = 2L, ["Caption"] = text }));
        await TestData.ExpectValidationFailedAsync("Caption", () => noteRecords.InsertAsync(new LatticeRecord(noteRecords.EntityType) { ["Id"] = 1L, ["Caption"] = text }));

        // Added to a batch.
        var batch = database.CreateWriteBatch();
        TestData.ExpectValidationFailed("Caption", () => batch.Upsert(new Note { Id = 2, Caption = text }));
        Assert.Equal(0, batch.Count);

        // A Binary value, judged by its length.
        await TestData.ExpectValidationFailedAsync("Attachment", () => notes.UpsertAsync(new Note { Id = 2, Attachment = GC.AllocateUninitializedArray<byte>(ValueBound.Bytes + 1) }));

        // Nothing was stored, and the stored entity is as it was.
        Assert.True(notes.TryGet(1L, out var stored, out var after));
        Assert.Equal("kept", stored.Caption);
        Assert.Equal(revision, after);
        Assert.Null(notes.Get(2L));
        Assert.Null(notes.Get(404L));
    }

    [Fact]
    public async Task Value_of_exactly_the_bound_of_one_value_passes_the_validation()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var notes = database.Set<Note>();

        // The write reaches its condition: the key is absent, so it is NotFound.
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => notes.ReplaceAsync(new Note { Id = 404, Caption = ValueBound.TextOneByteAboveInUtf8[..^1] }));
    }

    [Fact]
    public async Task In_a_UTF16_schema_134086688_characters_pass_the_validation_and_one_more_is_ValidationFailed()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Utf16Schema);
        var notes = database.Set<Note>();
        const int Characters = ValueBound.Bytes / 2;

        // One character more than the bound. The text is of NUL characters, which the runtime does not
        // write when it makes the string, and it is refused for its length before anything reads it, so
        // its memory is never touched, as that of the Binary value of the first test.
        await TestData.ExpectValidationFailedAsync("Caption", () => notes.ReplaceAsync(new Note { Id = 404, Caption = new string('\0', Characters + 1) }));

        // Exactly the bound: the write reaches its condition, the key is absent, so it is NotFound.
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => notes.ReplaceAsync(new Note { Id = 404, Caption = new string('a', Characters) }));
    }

    private sealed class Note
    {
        public long Id { get; set; }

        public string? Caption { get; set; }

        public byte[]? Attachment { get; set; }
    }
}
