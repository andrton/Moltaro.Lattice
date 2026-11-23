namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// Reads return owned copies at every depth (AGENTS.md, principle 3; docs/decisions/memory-and-access.md):
// - two reads of one entity share nothing, neither a collection, an owned value nor a byte array, at any
//   depth: a change made through one is not seen through the other, nor by a later read;
// - nothing a caller changes after a write returned changes what was written: neither the record or the
//   object it wrote, nor the collections, owned values and byte arrays it gave or that the record returned
//   (docs/decisions/mutation-commands.md, "Stable write inputs until completion"; the remarks of
//   LatticeRecord).
// By name and through the class, with NestedSchema.Full, which holds a value at every depth.
public sealed class SharedNothingTests
{
    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Two_reads_of_one_entity_by_name_share_nothing_at_any_depth()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var persons = database.Set("Person");
        await persons.InsertAsync(NestedSchema.RecordOf(database.Schema, NestedSchema.Full("ada")));
        var stored = NestedSchema.Describe(persons.Get("ada"));

        var first = persons.Get("ada")!;
        var second = persons.Get("ada")!;

        // No object of one read is an object of the other, at any depth.
        var home = first.Get<LatticeOwnedValue>("Home")!;
        Assert.NotSame(home, second.Get<LatticeOwnedValue>("Home"));
        Assert.NotSame(home.Get<List<string>>("Lines"), second.Get<LatticeOwnedValue>("Home")!.Get<List<string>>("Lines"));
        Assert.NotSame(home.Get<LatticeOwnedValue>("Geo"), second.Get<LatticeOwnedValue>("Home")!.Get<LatticeOwnedValue>("Geo"));
        Assert.NotSame(first.Get<List<string>>("Tags"), second.Get<List<string>>("Tags"));
        Assert.NotSame(first.Get<List<LatticeOwnedValue>>("Places")![0], second.Get<List<LatticeOwnedValue>>("Places")![0]);
        Assert.NotSame(first.Get<List<List<int>>>("Grid")![2], second.Get<List<List<int>>>("Grid")![2]);
        Assert.NotSame(first.Get<byte[]>("Photo"), second.Get<byte[]>("Photo"));
        Assert.NotSame(first.Get<List<byte[]>>("Files")![1], second.Get<List<byte[]>>("Files")![1]);

        // A change at every depth of the first read is seen neither through the second nor by a new read.
        home["City"] = "changed";
        home.Get<List<string>>("Lines")!.Add("changed");
        home.Get<LatticeOwnedValue>("Geo")!["Lat"] = 0.0;
        first.Get<List<string>>("Tags")!.Reverse();
        first.Get<List<long>>("Scores")!.Clear();
        first.Get<List<LatticeOwnedValue>>("Places")![0].Get<List<string>>("Lines")!.Add("changed");
        first.Get<List<List<int>>>("Grid")![2].Add(99);
        first.Get<byte[]>("Photo")![0] = 0xFF;
        first.Get<List<byte[]>>("Files")![1][0] = 0xFF;

        Assert.Equal(stored, NestedSchema.Describe(second));
        Assert.Equal(stored, NestedSchema.Describe(persons.Get("ada")));
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Two_reads_of_one_entity_through_the_class_share_nothing_at_any_depth()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var persons = database.Set<Person>();
        await persons.InsertAsync(NestedSchema.Full("ada"));
        var stored = NestedSchema.Describe(database.Schema, persons.Get("ada"));

        var first = persons.Get("ada")!;
        var second = persons.Get("ada")!;

        Assert.NotSame(first.Home, second.Home);
        Assert.NotSame(first.Home!.Lines, second.Home!.Lines);
        Assert.NotSame(first.Home.Geo, second.Home.Geo);
        Assert.NotSame(first.Work, second.Work);
        Assert.NotSame(first.Tags, second.Tags);
        Assert.NotSame(first.Places[0], second.Places[0]);
        Assert.NotSame(first.Places[0].Lines, second.Places[0].Lines);
        Assert.NotSame(first.Pins[0], second.Pins[0]);
        Assert.NotSame(first.Grid[2], second.Grid[2]);
        Assert.NotSame(first.Photo, second.Photo);
        Assert.NotSame(first.Files[1], second.Files[1]);

        first.Home.City = "changed";
        first.Home.Lines.Add("changed");
        first.Home.Geo!.Lat = 0;
        first.Work.Zip = -1;
        first.Tags.Reverse();
        first.Marks.Add(7);
        first.Places[0].Lines.Add("changed");
        first.Pins[0].Lon = 0;
        first.Grid[2].Add(99);
        first.Photo![0] = 0xFF;
        first.Files[1][0] = 0xFF;

        Assert.Equal(stored, NestedSchema.Describe(database.Schema, second));
        Assert.Equal(stored, NestedSchema.Describe(database.Schema, persons.Get("ada")));
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Nothing_a_caller_changes_in_a_record_after_its_write_returned_changes_what_was_written()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var persons = database.Set("Person");
        var given = NestedSchema.Full("ada");
        var record = NestedSchema.RecordOf(database.Schema, given);
        var written = NestedSchema.Describe(record);

        await persons.InsertAsync(record);

        // What the record returns is its own, and so is a change through it; what it was given is the
        // caller's. Neither is what the database holds.
        var home = record.Get<LatticeOwnedValue>("Home")!;
        home["Zip"] = 0;
        home.Get<List<string>>("Lines")!.Clear();
        home.Get<LatticeOwnedValue>("Geo")!["Lon"] = 0.0;
        record.Get<List<string>>("Tags")!.Add("changed");
        record.Get<List<LatticeOwnedValue>>("Places")![1]["City"] = "changed";
        record.Get<List<List<int>>>("Grid")![1][0] = 99;
        record.Get<byte[]>("Photo")![0] = 0xFF;
        record.Get<List<byte[]>>("Files")![2][1] = 0xFF;
        record["Name"] = "changed";
        given.Tags.Add("changed");
        given.Grid[2].Clear();
        given.Photo![1] = 0xFF;

        Assert.Equal(written, NestedSchema.Describe(persons.Get("ada")));
        Assert.Equal(written, NestedSchema.Describe(database.Schema, database.Set<Person>().Get("ada")));
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Nothing_a_caller_changes_in_an_object_after_its_write_returned_changes_what_was_written()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var persons = database.Set<Person>();
        var person = NestedSchema.Full("ada");
        var written = NestedSchema.Describe(database.Schema, person);

        await persons.InsertAsync(person);

        person.Name = "changed";
        person.Home!.City = "changed";
        person.Home.Lines.Add("changed");
        person.Home.Geo!.Lat = 0;
        person.Home = null;
        person.Work.Lines.Add("changed");
        person.Tags.Clear();
        person.Scores[0] = 0;
        person.Places[0].Zip = 0;
        person.Places.RemoveAt(1);
        person.Pins[0].Lat = 0;
        person.Grid[2][0] = 99;
        person.Photo![0] = 0xFF;
        person.Files[2][0] = 0xFF;

        Assert.Equal(written, NestedSchema.Describe(database.Schema, persons.Get("ada")));
        Assert.Equal(written, NestedSchema.Describe(database.Set("Person").Get("ada")));
    }
}
