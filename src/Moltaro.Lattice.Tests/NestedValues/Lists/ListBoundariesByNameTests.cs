namespace Moltaro.Lattice.Tests.NestedValues.Lists;

// A packed List keeps the supplied order and its nulls on either side of where it lies, written
// by name or through the class and read by name (docs/design/milestone-1-engine-contract.md, "Collections and
// strings" and "Native layout and allocation"). The figures come from the layout: a value of at most 64 bytes
// lies inline and one of at most 4,096 bytes in one block, so 8 and 9 Int64 lie inline and not, 512 and 513
// in one block and not; 12 and 13 nullable Int32, a record of 5 bytes, 819 and 820, the same boundaries. The
// boundary of a chunk is ListChunkBoundaryTests'.
public sealed class ListBoundariesByNameTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(512)]
    [InlineData(513)]
    [InlineData(819)]
    [InlineData(820)]
    public async Task Packed_Lists_on_either_side_of_lying_inline_and_of_lying_in_one_block_are_read_back_by_name(int count)
    {
        var person = NestedSchema.Full($"sizes {count}");
        person.Scores = [.. Enumerable.Range(0, count).Select(index => (long)(count - index) * 7_919)];
        person.Marks = [.. Enumerable.Range(0, count).Select(index => index % 3 == 0 ? (int?)null : -index)];
        person.Home!.Lines = [.. Enumerable.Range(0, count % 50).Select(index => $"{index}")];
        person.Grid = [[.. Enumerable.Range(0, count)], [.. Enumerable.Range(0, count / 2)]];

        await ExpectReadBackByNameAsync(person);
    }

    // Writes the person by name and, under another code, through the class, and reads both back by name.
    private static async Task ExpectReadBackByNameAsync(Person person)
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");

        await byName.InsertAsync(NestedSchema.RecordOf(database.Schema, person));

        Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get(person.Code)));

        person.Code += " through the class";

        await database.Set<Person>().InsertAsync(person);

        Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get(person.Code)));
    }
}
