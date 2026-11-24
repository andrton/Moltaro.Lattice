using Moltaro.Lattice.TestSupport.Values;

namespace Moltaro.Lattice.Tests.NestedValues.Bounds;

// One String or Binary value takes at most 268,173,376 bytes of its stored encoding at every position: a
// field of an owned value and an element of a List have the bound a field of the entity has (the remarks of
// String and Binary of LatticeFieldTypeEnum, decision O19). A write of a larger value at any depth, and
// adding an entity with one to a write batch, is ValidationFailed before the conditions of the write are
// looked at, as a write of one in a field of the entity is (ValueBoundWriteTests), and the refusal names the
// place of the value as the validation of a write names a place: by the type that declares the field and the
// field, an owned type and its field for a field of an owned value, the entity type and the field that holds
// the List for an element. A value of exactly the bound passes the validation, and the write reaches its
// condition.
//
// The schema: Note, whose key Code is a String, holds Body, an owned Attachment of a nullable String Caption
// and a nullable Binary Data, and Captions, a List of String, and Blobs, a List of Binary. The text is that of
// ValueBound, 268,173,377 bytes in UTF-8, and without its last character exactly the bound; a Binary value
// above the bound is made inside its write and refused for its length, so that its memory is never touched.
// The bound is a number of bytes of the stored encoding whatever the storage of the text: in a schema of the
// UTF-16 encoding whose Caption and Captions store their text in the dictionary, it is 134,086,688
// characters, a text of NUL characters, which the runtime does not write when it makes the string, one
// character more. The class runs alone (LargeValueCollection).
[Collection<LargeValueCollection>]
public sealed class DepthBoundTests
{
    private static readonly LatticeSchema Schema = SchemaOf("Utf8", "Plain");

    private static readonly LatticeSchema Utf16Dictionary = SchemaOf("Utf16", "Dictionary");

    [Fact]
    public async Task String_and_Binary_above_their_bound_in_an_owned_value_are_ValidationFailed_with_their_place_named()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var byName = database.Set("Note");
        var byClass = database.Set<Note>();
        var text = ValueBound.TextOneByteAboveInUtf8;

        // By name, an insert of an absent key, and through the class, a replace of an absent key, which would
        // otherwise be NotFound.
        var record = RecordOf(byName, "above");
        record.Get<LatticeOwnedValue>("Body")!["Caption"] = text;
        await ExpectRefusedAsync("Attachment", "Caption", () => byName.InsertAsync(record));
        await ExpectRefusedAsync("Attachment", "Caption", () => byClass.ReplaceAsync(new Note { Code = "absent", Body = new Attachment { Caption = text } }));
        await ExpectRefusedAsync("Attachment", "Data", () => byClass.UpsertAsync(new Note { Code = "above", Body = new Attachment { Data = GC.AllocateUninitializedArray<byte>(ValueBound.Bytes + 1) } }));

        // Added to a batch.
        var batch = database.CreateWriteBatch();
        var refused = Assert.Throws<LatticeException>(() => batch.Insert(new Note { Code = "above", Body = new Attachment { Caption = text } }));
        ExpectPlace(refused, "Attachment", "Caption");
        Assert.Equal(0, batch.Count);

        Assert.Null(byName.Get("above"));
    }

    [Fact]
    public async Task String_and_Binary_above_their_bound_as_an_element_of_a_List_are_ValidationFailed_with_their_place_named()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var byName = database.Set("Note");
        var byClass = database.Set<Note>();
        var text = ValueBound.TextOneByteAboveInUtf8;

        var record = RecordOf(byName, "above");
        record["Captions"] = new[] { "fine", text };
        await ExpectRefusedAsync("Note", "Captions", () => byName.InsertAsync(record));
        await ExpectRefusedAsync("Note", "Captions", () => byClass.ReplaceAsync(new Note { Code = "absent", Captions = [text] }));
        await ExpectRefusedAsync("Note", "Blobs", () => byClass.UpsertAsync(new Note { Code = "above", Blobs = [[1], GC.AllocateUninitializedArray<byte>(ValueBound.Bytes + 1)] }));

        var batch = database.CreateWriteBatch();
        var refused = Assert.Throws<LatticeException>(() => batch.Upsert(new Note { Code = "above", Captions = [text] }));
        ExpectPlace(refused, "Note", "Captions");
        Assert.Equal(0, batch.Count);

        Assert.Null(byName.Get("above"));
    }

    [Fact]
    public async Task In_a_UTF16_schema_with_Dictionary_storage_134086688_characters_at_depth_pass_the_validation_and_one_more_is_ValidationFailed()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Utf16Dictionary);
        var byClass = database.Set<Note>();
        const int Characters = ValueBound.Bytes / 2;

        await ExpectRefusedAsync("Attachment", "Caption", () => byClass.ReplaceAsync(new Note { Code = "absent", Body = new Attachment { Caption = new string('\0', Characters + 1) } }));
        await ExpectRefusedAsync("Note", "Captions", () => byClass.ReplaceAsync(new Note { Code = "absent", Captions = [new string('\0', Characters + 1)] }));

        // Exactly the bound: the write reaches its condition, the key is absent, so it is NotFound.
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => byClass.ReplaceAsync(new Note { Code = "absent", Body = new Attachment { Caption = new string('a', Characters) } }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => byClass.ReplaceAsync(new Note { Code = "absent", Captions = [new string('a', Characters)] }));
    }

    [Fact]
    public async Task String_and_Binary_of_exactly_their_bound_at_depth_pass_the_validation()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var byClass = database.Set<Note>();
        var text = ValueBound.TextOneByteAboveInUtf8[..^1];

        // Each write reaches its condition: the key is absent, so it is NotFound.
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => byClass.ReplaceAsync(new Note { Code = "absent", Body = new Attachment { Caption = text } }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => byClass.ReplaceAsync(new Note { Code = "absent", Captions = [text] }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => byClass.ReplaceAsync(new Note { Code = "absent", Body = new Attachment { Data = new byte[ValueBound.Bytes] } }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.NotFound, () => byClass.ReplaceAsync(new Note { Code = "absent", Blobs = [new byte[ValueBound.Bytes]] }));
    }

    // The schema of Note, in the encoding given, with Caption and the elements of Captions in the storage given.
    private static LatticeSchema SchemaOf(string encoding, string storage) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Attachment", "ClrType": "{{typeof(Attachment).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Caption", "Type": "String", "Nullable": true, "StringStorage": "{{storage}}" },
                { "Id": 2, "Name": "Data", "Type": "Binary", "Nullable": true }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Body", "Type": "Owned", "OwnedType": "Attachment" },
                { "Id": 3, "Name": "Captions", "Type": "List", "Element": { "Type": "String", "StringStorage": "{{storage}}" } },
                { "Id": 4, "Name": "Blobs", "Type": "List", "Element": { "Type": "Binary" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private static LatticeRecord RecordOf(ILatticeSet notes, string code)
    {
        Assert.True(Schema.TryGetOwnedType("Attachment", out var attachment));

        return new LatticeRecord(notes.EntityType)
        {
            ["Code"] = code,
            ["Body"] = new LatticeOwnedValue(attachment),
            ["Captions"] = Array.Empty<string>(),
            ["Blobs"] = Array.Empty<byte[]>(),
        };
    }

    private static async Task ExpectRefusedAsync(string owner, string field, Func<ValueTask<LatticeWriteResult>> write)
    {
        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, write);

        ExpectPlace(refused, owner, field);
    }

    // The refusal names the type that declares the field and the field.
    private static void ExpectPlace(LatticeException refused, string owner, string field)
    {
        Assert.Equal(LatticeErrorEnum.ValidationFailed, refused.Error);
        Assert.Contains(owner, refused.Message, StringComparison.Ordinal);
        Assert.Contains(field, refused.Message, StringComparison.Ordinal);
    }
}
