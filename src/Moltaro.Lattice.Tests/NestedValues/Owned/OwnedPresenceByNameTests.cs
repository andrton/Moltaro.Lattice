namespace Moltaro.Lattice.Tests.NestedValues.Owned;

// A nullable owned value is present or absent as the last write left it, read by name: Home of a person
// written absent, then replaced present with its own owned value and List, absent, present with an empty List
// and without its owned value, present again with both, and absent, each state read back whole.
public sealed class OwnedPresenceByNameTests
{
    [Fact]
    public async Task Owned_value_that_comes_and_goes_across_replaces_is_read_back_present_or_absent_by_name()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var person = NestedSchema.Full("changing");

        person.Home = null;
        await byName.InsertAsync(NestedSchema.RecordOf(database.Schema, person));

        Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get("changing")));

        Address?[] homes =
        [
            new Address { City = "Oslo", Zip = 150, Lines = ["a"], Geo = new Point { Lat = 1, Lon = 2 } },
            null,
            new Address { City = "Bodø", Zip = 8006, Lines = [] },
            new Address { City = "Bodø", Zip = 8006, Lines = ["b", "c"], Geo = new Point { Lat = 67.28, Lon = 14.4 } },
            null,
        ];

        foreach (var home in homes)
        {
            person.Home = home;
            await byName.ReplaceAsync(NestedSchema.RecordOf(database.Schema, person));

            Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get("changing")));
        }
    }
}
