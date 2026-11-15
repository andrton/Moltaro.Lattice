namespace Moltaro.Lattice.Tests.Binding;

// An array property may hold, at run time, an array of arrays that the runtime lets stand for
// the declared one although its elements would be read as other values: an sbyte[][] in a
// byte[][] property, a uint[][] in an int[][] one. Such an array is refused at any depth when
// the object is written, also when it holds no array that could be refused when it is entered;
// an array of the declared type, empty as well, passes.
public sealed class NestedArrayPropertyTests
{
    private const string ListOfBinary = "{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Binary' } }";
    private const string ListOfListsOfInt32 = "{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'List', 'Element': { 'Type': 'Int32' } } }";

    [Fact]
    public async Task Empty_array_of_signed_byte_arrays_in_an_array_of_byte_arrays_is_refused()
    {
        await ExpectRefusedAsync(ListOfBinary, (byte[][])(object)Array.Empty<sbyte[]>());
        await ExpectWrittenAsFarAsAWriteGoesAsync(ListOfBinary, Array.Empty<byte[]>());
    }

    [Fact]
    public async Task Empty_array_of_unsigned_integer_arrays_in_an_array_of_integer_arrays_is_refused()
    {
        await ExpectRefusedAsync(ListOfListsOfInt32, (int[][])(object)Array.Empty<uint[]>());
        await ExpectWrittenAsFarAsAWriteGoesAsync(ListOfListsOfInt32, Array.Empty<int[]>());
    }

    // The object is refused with ValidationFailed through a set and when it is added to a write
    // batch.
    private static async Task ExpectRefusedAsync<TArray>(string field, TArray value)
    {
        await using var database = await TestData.OpenMasterAsync(schema: BindingSchema.For<BindingSchema.Holder<TArray>>(field));
        var holder = new BindingSchema.Holder<TArray> { Id = 1, Value = value };
        var batch = database.CreateWriteBatch();

        await TestData.ExpectErrorAsync(LatticeErrorEnum.ValidationFailed, () => database.Set<BindingSchema.Holder<TArray>>().InsertAsync(holder));
        TestData.ExpectError(LatticeErrorEnum.ValidationFailed, () => batch.Insert(holder));
        Assert.Equal(0, batch.Count);
    }

    // The object passes validation: a write batch takes it, and a set refuses the write only as
    // not implemented, which storing a List is.
    private static async Task ExpectWrittenAsFarAsAWriteGoesAsync<TArray>(string field, TArray value)
    {
        await using var database = await TestData.OpenMasterAsync(schema: BindingSchema.For<BindingSchema.Holder<TArray>>(field));
        var holder = new BindingSchema.Holder<TArray> { Id = 1, Value = value };
        var batch = database.CreateWriteBatch();

        batch.Insert(holder);
        Assert.Equal(1, batch.Count);
        await Assert.ThrowsAsync<NotSupportedException>(async () => await database.Set<BindingSchema.Holder<TArray>>().InsertAsync(holder));
    }
}
