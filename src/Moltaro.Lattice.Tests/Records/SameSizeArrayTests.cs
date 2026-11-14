namespace Moltaro.Lattice.Tests.Records;

// The runtime takes an array of one integer type for an array of another of the same size, and
// an array of an enum for one of its underlying type: a uint[] passes as an int[], an sbyte[] as
// a byte[]. A record takes an array for a collection, and a byte array for a Binary value, only
// when its element type is exactly the declared one; any other is refused with the field named,
// and its values are never read as other values. That holds at every position and depth, in a
// record and in an owned value, when a value is set and when a record is written.
public sealed class SameSizeArrayTests
{
    [Fact]
    public void Array_of_another_integer_type_of_the_same_size_is_refused_for_a_List_and_a_Set()
    {
        var record = new LatticeRecord(Holder(Schema()));

        ExpectRefused(record, "Counts", new uint[] { 3_000_000_000 });
        ExpectRefused(record, "Ids", new long[] { -1 });

        // The arrays of exactly the declared types are taken.
        record["Counts"] = new[] { 3 };
        record["Ids"] = new ulong[] { 18_446_744_073_709_551_615 };
        Assert.Equal([3], Assert.IsType<List<int>>(record["Counts"]));
        Assert.Contains(ulong.MaxValue, Assert.IsType<HashSet<ulong>>(record["Ids"]));
    }

    [Fact]
    public void Array_of_an_enum_is_refused_for_a_List_of_its_underlying_type_also_in_an_owned_value()
    {
        var schema = Schema();
        Assert.True(schema.TryGetOwnedType("Tally", out var tally));
        var record = new LatticeRecord(Holder(schema));
        var value = new LatticeOwnedValue(tally);

        ExpectRefused(record, "Counts", new[] { ConsoleColor.Red });
        var exception = Assert.Throws<ArgumentException>(() => value["Counts"] = new[] { ConsoleColor.Red });
        Assert.Contains("Counts", exception.Message);
        Assert.Null(value["Counts"]);
    }

    [Fact]
    public void Signed_byte_array_is_refused_for_a_Binary_value_at_every_position()
    {
        var record = new LatticeRecord(Holder(Schema()));

        // A field, an element of a List, a value of a Map.
        ExpectRefused(record, "Photo", new sbyte[] { -1, 2 });
        ExpectRefused(record, "Blobs", new[] { new sbyte[] { -1 } });
        ExpectRefused(record, "Blobs", new List<byte[]> { (byte[])(object)new sbyte[] { -1 } });
        ExpectRefused(record, "BlobsById", new Dictionary<int, byte[]> { [1] = (byte[])(object)new sbyte[] { -1 } });

        // A byte[] is taken.
        record["Photo"] = new byte[] { 255, 2 };
        Assert.IsType<byte[]>(record["Photo"]);
    }

    [Fact]
    public async Task Record_that_holds_a_signed_byte_array_through_a_returned_List_is_ValidationFailed_when_written()
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema());
        var holders = database.Set("Holder");
        var record = new LatticeRecord(holders.EntityType) { ["Id"] = 1L, ["Blobs"] = Array.Empty<byte[]>() };

        // A List<byte[]> takes an sbyte[] when it is cast through object, without a check.
        record.Get<List<byte[]>>("Blobs")!.Add((byte[])(object)new sbyte[] { -1 });

        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => holders.InsertAsync(record));
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => database.CreateWriteBatch().Insert(record));
    }

    [Fact]
    public async Task Signed_byte_array_is_refused_for_a_Binary_field_of_a_writable_entity_type_and_nothing_is_stored()
    {
        await using var database = await TestData.OpenMasterAsync();
        var settings = database.Set("Setting");
        var record = new LatticeRecord(settings.EntityType) { ["Name"] = "theme", ["Value"] = "dark", ["Priority"] = 1 };

        Assert.Throws<ArgumentException>(() => record["Payload"] = new sbyte[] { -1, 2 });

        // The refused value is not held, and the record written without it stores no payload.
        Assert.Null(record["Payload"]);
        await settings.InsertAsync(record);
        Assert.Null(settings.Get("theme")!["Payload"]);
    }

    private static void ExpectRefused(LatticeRecord record, string fieldName, object value)
    {
        var before = record[fieldName];

        var exception = Assert.Throws<ArgumentException>(() => record[fieldName] = value);

        Assert.Contains(fieldName, exception.Message);
        Assert.Same(before, record[fieldName]);
    }

    private static LatticeEntityType Holder(LatticeSchema schema)
    {
        Assert.True(schema.TryGetEntityType("Holder", out var holder));
        return holder;
    }

    // Holder has collections of integers and of byte arrays and a Binary field; the owned type
    // Tally has a List of Int32.
    private static LatticeSchema Schema() => LatticeSchema.Compile("""
        {
          "OwnedTypes": [
            { "Id": 1, "Name": "Tally", "Fields": [ { "Id": 1, "Name": "Counts", "Type": "List", "Element": { "Type": "Int32" } } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Counts", "Type": "List", "Element": { "Type": "Int32" }, "Nullable": true },
                { "Id": 3, "Name": "Ids", "Type": "Set", "Element": { "Type": "UInt64" }, "Nullable": true },
                { "Id": 4, "Name": "Photo", "Type": "Binary", "Nullable": true },
                { "Id": 5, "Name": "Blobs", "Type": "List", "Element": { "Type": "Binary" }, "Nullable": true },
                { "Id": 6, "Name": "BlobsById", "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "Binary" }, "Nullable": true },
                { "Id": 7, "Name": "Tally", "Type": "Owned", "OwnedType": "Tally", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);
}
