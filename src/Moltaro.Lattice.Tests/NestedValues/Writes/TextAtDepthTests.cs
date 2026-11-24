namespace Moltaro.Lattice.Tests.NestedValues.Writes;

// Text that is not well-formed, a surrogate without its pair, is refused at any depth as in a field of the
// entity: a line of the second Address of Places, a List of owned values whose Lines is a List of Strings, is
// ValidationFailed with the field named, and nothing of the entity is written.
public sealed class TextAtDepthTests
{
    [Fact]
    public async Task Text_that_is_not_well_formed_deep_in_a_tree_is_ValidationFailed_with_the_field_named_and_nothing_is_written()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var record = NestedSchema.RecordOf(database.Schema, NestedSchema.Full("malformed"));

        record.Get<List<LatticeOwnedValue>>("Places")![1].Get<List<string>>("Lines")!.Add("a\uD800b");

        await TestData.ExpectValidationFailedAsync("Lines", () => byName.InsertAsync(record));

        Assert.Null(byName.Get("malformed"));
    }
}
