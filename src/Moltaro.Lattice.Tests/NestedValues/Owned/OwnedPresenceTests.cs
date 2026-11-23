namespace Moltaro.Lattice.Tests.NestedValues.Owned;

// A nullable owned value may be absent (LatticeFieldTypeEnum.Owned), at its own depth and inside another
// owned value, through a class and by name: Home of a Person is absent, or present with its Geo absent, or
// present with its Geo. An absent owned value is null when it is read back, an owned value that is not
// nullable is never absent, and an owned value replaced by an absent one and back is read back each time
// as it was written (NestedSchema).
public sealed class OwnedPresenceTests
{
    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Nullable_owned_value_is_read_back_absent_or_present_at_each_depth_by_name_and_through_the_class()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var byClass = database.Set<Person>();

        var withoutHome = NestedSchema.Full("without home");
        withoutHome.Home = null;

        var withoutGeo = NestedSchema.Full("without geo");
        withoutGeo.Home!.Geo = null;

        var withGeo = NestedSchema.Full("with geo");

        await byClass.InsertAsync(withoutHome);
        await byName.InsertAsync(NestedSchema.RecordOf(database.Schema, withoutGeo));
        await byClass.InsertAsync(withGeo);

        Assert.Null(byClass.Get("without home")!.Home);
        Assert.Null(byName.Get("without home")!["Home"]);
        Assert.Null(byClass.Get("without geo")!.Home!.Geo);
        Assert.Null(byName.Get("without geo")!.Get<LatticeOwnedValue>("Home")!["Geo"]);
        Assert.Equal(59.91, byClass.Get("with geo")!.Home!.Geo!.Lat);

        foreach (var person in new[] { withoutHome, withoutGeo, withGeo })
        {
            Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get(person.Code)));
            Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(database.Schema, byClass.Get(person.Code)));
        }
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Absent_owned_value_replaced_by_a_present_one_and_back_is_read_back_each_time()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var byClass = database.Set<Person>();
        var person = NestedSchema.Full("changing");
        person.Home = null;

        await byClass.InsertAsync(person);

        // Present with its Geo, absent, present without its Geo, present with it, absent: through the class
        // and by name in turn.
        Address?[] homes =
        [
            new Address { City = "Oslo", Zip = 150, Lines = ["a"], Geo = new Point { Lat = 1, Lon = 2 } },
            null,
            new Address { City = "Bodø", Zip = 8006, Lines = [] },
            new Address { City = "Bodø", Zip = 8006, Lines = ["b", "c"], Geo = new Point { Lat = 67.28, Lon = 14.4 } },
            null,
        ];

        for (var step = 0; step < homes.Length; step++)
        {
            person.Home = homes[step];

            if (step % 2 == 0)
            {
                await byClass.ReplaceAsync(person);
            }
            else
            {
                await byName.ReplaceAsync(NestedSchema.RecordOf(database.Schema, person));
            }

            Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get("changing")));
            Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(database.Schema, byClass.Get("changing")));
        }
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Owned_value_that_is_not_nullable_is_required_and_read_back_with_its_values()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var person = NestedSchema.Full("work");
        var record = NestedSchema.RecordOf(database.Schema, person);

        record["Work"] = null;

        await TestData.ExpectValidationFailedAsync("Work", () => byName.InsertAsync(record));

        // An owned value whose fields are all at their defaults is a value, not an absence.
        person.Work = new Address();
        await database.Set<Person>().InsertAsync(person);

        var work = byName.Get("work")!.Get<LatticeOwnedValue>("Work")!;
        Assert.Equal("", work["City"]);
        Assert.Equal(0, work["Zip"]);
        Assert.Empty(work.Get<List<string>>("Lines")!);
        Assert.Null(work["Geo"]);
    }
}
