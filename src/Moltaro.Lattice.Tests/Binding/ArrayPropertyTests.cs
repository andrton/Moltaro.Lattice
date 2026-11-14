namespace Moltaro.Lattice.Tests.Binding;

// A property of an array type binds by its declared type, and at run time it may hold an array
// of another element type, which the runtime takes for it: an sbyte[] in a byte[] property, a
// uint[] in an int[] one. Such an object is refused with ValidationFailed when it is written, and
// nothing of it is stored. A multi-dimensional array is not a List, and the message of its
// refusal names it as what it is.
public sealed class ArrayPropertyTests
{
    [Fact]
    public async Task Byte_array_property_that_holds_a_signed_byte_array_is_ValidationFailed_and_nothing_is_stored()
    {
        await using var database = await TestData.OpenMasterAsync(schema: LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Document", "ClrType": "{{typeof(Document).FullName}}",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Content", "Type": "Binary", "Nullable": true } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """));
        var documents = database.Set<Document>();
        var document = new Document { Id = 1, Content = (byte[])(object)new sbyte[] { -1, 2 } };

        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => documents.InsertAsync(document));
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => database.CreateWriteBatch().Insert(document));
        Assert.Empty(documents.Enumerate());

        // The same object with a byte[] is written.
        document.Content = [255, 2];
        await documents.InsertAsync(document);
        Assert.Equal(new byte[] { 255, 2 }, documents.Get(1L)!.Content);
    }

    [Fact]
    public async Task Array_property_that_holds_an_array_of_another_integer_type_is_ValidationFailed()
    {
        await using var database = await TestData.OpenMasterAsync(schema: BindingSchema.For<BindingSchema.Holder<int[]>>("{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Int32' } }"));
        var batch = database.CreateWriteBatch();

        var exception = Assert.Throws<LatticeException>(() => batch.Insert(new BindingSchema.Holder<int[]> { Id = 1, Value = (int[])(object)new uint[] { 3_000_000_000 } }));

        Assert.Equal(LatticeErrorEnum.ValidationFailed, exception.Error);
        Assert.Contains("UInt32[]", exception.Message);
        Assert.Equal(0, batch.Count);

        batch.Insert(new BindingSchema.Holder<int[]> { Id = 1, Value = [3] });
        Assert.Equal(1, batch.Count);
    }

    [Fact]
    public async Task Multi_dimensional_array_is_refused_with_a_message_that_names_it()
    {
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<long[,]>>("{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Int64' } }", "Value", "Int64[,]");
    }

    private sealed class Document
    {
        public long Id { get; set; }

        public byte[]? Content { get; set; }
    }
}
