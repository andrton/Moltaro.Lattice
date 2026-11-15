namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// Until S09 makes it an option, the engine stores an entity of at most 1 MiB, and a write of a
// larger one is refused with WriteRejected (brief of Epic 1, sprint S06, "The limits before S09").
// The size of an entity is its fixed part and the encoded bytes of its String and Binary values,
// wherever they lie; an entity of exactly the limit is stored, one byte more is refused (brief of
// S05, "The size of an entity"). The placeholder has no such limit, so these tests run on the engine
// only.
//
// Blob has the key Id, an Int64, and one field: by the formula of the brief of S05 ("The fixed
// part"), its fixed part is the 32 bytes of the header, the 8 bytes of Id and one descriptor of 16
// bytes, 56 bytes.
public sealed class EntitySizeLimitTests
{
    private const int Limit = 1024 * 1024;

    private const int FixedSize = 32 + 8 + 16;

    [Fact(Skip = NotImplemented.EngineDatabase)]
    public async Task Entity_of_exactly_1_MiB_is_stored_and_one_byte_more_is_WriteRejected()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, Schema("Binary", typeof(BinaryBlob)));
        var blobs = database.Set<BinaryBlob>();
        var records = database.Set("Blob");
        var largest = new byte[Limit - FixedSize];
        largest.AsSpan().Fill(0x5A);
        var tooLarge = new byte[Limit - FixedSize + 1];

        await blobs.InsertAsync(new BinaryBlob { Id = 1, Value = largest });
        Assert.Equal(largest, blobs.Get(1L)!.Value);

        // As a new entity and as a new state of one that exists, through the class and by name.
        await TestData.ExpectErrorAsync(LatticeErrorEnum.WriteRejected, () => blobs.InsertAsync(new BinaryBlob { Id = 2, Value = tooLarge }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.WriteRejected, () => blobs.UpsertAsync(new BinaryBlob { Id = 1, Value = tooLarge }));
        await TestData.ExpectErrorAsync(LatticeErrorEnum.WriteRejected, () => records.ReplaceAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Value"] = tooLarge }));

        Assert.Null(blobs.Get(2L));
        Assert.Equal(largest, blobs.Get(1L)!.Value);
    }

    [Fact(Skip = NotImplemented.EngineDatabase)]
    public async Task Size_of_a_String_is_its_encoded_bytes()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, Schema("String"));
        var texts = database.Set("Blob");

        // € is three bytes in UTF-8: 56 + 3 × 349,506 bytes are stored, three more are not.
        var largest = new string('€', 349_506);
        var tooLarge = new string('€', 349_507);

        await texts.InsertAsync(new LatticeRecord(texts.EntityType) { ["Id"] = 1L, ["Value"] = largest });
        Assert.Equal(largest, texts.Get(1L)!["Value"]);

        await TestData.ExpectErrorAsync(LatticeErrorEnum.WriteRejected, () => texts.InsertAsync(new LatticeRecord(texts.EntityType) { ["Id"] = 2L, ["Value"] = tooLarge }));
        Assert.Null(texts.Get(2L));
    }

    // The entity type Blob with the field Value of the given type, bound to the class given.
    private static LatticeSchema Schema(string type, Type? clrType = null)
    {
        var binding = clrType is null ? "" : $"\"ClrType\": \"{clrType.FullName}\",";

        return LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Blob", {{binding}}
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Value", "Type": "{{type}}" } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
    }

    private sealed class BinaryBlob
    {
        public long Id { get; set; }

        public byte[] Value { get; set; } = [];
    }
}
