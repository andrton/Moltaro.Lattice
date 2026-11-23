namespace Moltaro.Lattice.Tests.NestedValues.Replication;

// A child loads entities with owned values, Lists and references and applies their replacement, so that it
// reads every tree as its master does (docs/design/milestone-1-engine-contract.md, "Replication transport
// and initial loading"): what travels holds no address, and the child stores what it receives in blocks of
// its own. The trees are NestedSchema.Full, with a value at every depth, and trees of other shapes, Lists in
// chunks of both forms among them; each is compared whole, by name and through the class, on the master and
// on the child. The child connects to its master over loopback TCP.
public sealed class ChildTreeTests
{
    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Child_loads_entities_with_owned_values_Lists_and_references_as_its_master_holds_them()
    {
        await using var master = await NestedSchema.OpenListeningMasterAsync();
        var persons = master.Set<Person>();

        foreach (var person in Trees())
        {
            await persons.InsertAsync(person);
        }

        await using var child = await TestDatabases.OpenChildAsync(master);

        ExpectSameTrees(master, child);
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Child_applies_the_replacement_and_the_delete_of_such_entities()
    {
        await using var master = await NestedSchema.OpenListeningMasterAsync();
        var persons = master.Set("Person");
        var trees = Trees();

        foreach (var person in trees)
        {
            await persons.InsertAsync(NestedSchema.RecordOf(master.Schema, person));
        }

        await using var child = await TestDatabases.OpenChildAsync(master);

        // Each tree replaced by the one after it, of another shape, then the first deleted.
        var codes = trees.Select(person => person.Code).ToArray();

        for (var index = 0; index < trees.Length; index++)
        {
            var replacement = NestedSchema.RecordOf(master.Schema, trees[(index + 1) % trees.Length]);

            replacement["Code"] = codes[index];
            await persons.ReplaceAsync(replacement);
        }

        var last = await persons.DeleteAsync(codes[0]);
        await child.WaitForPositionAsync(last.Position, TimeSpan.FromMinutes(1));

        Assert.Null(child.Set("Person").Get(codes[0]));
        ExpectSameTrees(master, child);
    }

    // Trees of several shapes, each under a key of its own.
    private static Person[] Trees()
    {
        var full = NestedSchema.Full("full");

        var bare = new Person { Code = "bare", Name = "bare" };

        var chunked = NestedSchema.Full("chunked");
        chunked.Scores = [.. Enumerable.Range(0, 20_000).Select(value => (long)value)];
        chunked.Tags = [.. Enumerable.Range(0, 2_000).Select(value => $"tag {value}")];
        chunked.Home!.Lines = [new string('l', 70_000), "", "short"];
        chunked.Places = [.. Enumerable.Range(0, 400).Select(value => new Address { City = $"city {value}", Zip = value, Geo = value % 2 == 0 ? new Point { Lat = value } : null })];
        chunked.Files = [new byte[70_000], [1]];

        var withoutHome = NestedSchema.Full("without home");
        withoutHome.Home = null;
        withoutHome.Manager = null;

        return [full, bare, chunked, withoutHome];
    }

    // The child holds every entity of its master, each with its whole tree, by name and through the class.
    private static void ExpectSameTrees(ILatticeDatabase master, ILatticeDatabase child)
    {
        var onMaster = master.Set("Person").Enumerate().ToDictionary(record => record.Get<string>("Code")!, NestedSchema.Describe);
        var onChild = child.Set("Person").Enumerate().ToDictionary(record => record.Get<string>("Code")!, NestedSchema.Describe);

        Assert.Equal(onMaster.Keys.Order(StringComparer.Ordinal), onChild.Keys.Order(StringComparer.Ordinal));

        foreach (var (code, tree) in onMaster)
        {
            Assert.Equal(tree, onChild[code]);
            Assert.Equal(tree, NestedSchema.Describe(child.Schema, child.Set<Person>().Get(code)));
        }
    }
}
