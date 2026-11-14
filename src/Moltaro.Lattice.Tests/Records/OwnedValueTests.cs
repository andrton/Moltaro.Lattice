namespace Moltaro.Lattice.Tests.Records;

// An owned value without a .NET class: the values of the fields of its owned type, by field
// name, checked as the fields of a record are. A record holds one at every position of an
// owned type, also inside a List or a Map, and whether or not the owned type names a class; it
// accepts one of that owned type only.
public sealed class OwnedValueTests
{
    [Fact]
    public void Owned_value_needs_an_owned_type()
    {
        Assert.Throws<ArgumentNullException>(() => new LatticeOwnedValue(null!));
    }

    [Fact]
    public void Owned_value_holds_the_fields_of_its_owned_type()
    {
        var address = ValueSchema.OwnedType(ValueSchema.Compile(), "Address");

        var value = new LatticeOwnedValue(address) { ["City"] = "Oslo" };

        Assert.Same(address, value.OwnedType);
        Assert.Equal("Oslo", value["City"]);
        Assert.Equal("Oslo", value.Get<string>("City"));

        // A field without a value is null, and Get gives the default for it.
        Assert.Null(value["Street"]);
        Assert.Null(value.Get<short?>("Floor"));

        // An integer of another width is accepted when it fits.
        value["Floor"] = 3;
        Assert.Equal((short)3, value["Floor"]);
        Assert.Throws<ArgumentException>(() => value["Floor"] = 40_000);
    }

    [Fact]
    public void Owned_value_refuses_a_field_its_owned_type_does_not_have_and_a_value_that_does_not_fit()
    {
        var schema = ValueSchema.Compile();
        var value = new LatticeOwnedValue(ValueSchema.OwnedType(schema, "Address"));

        Assert.Throws<ArgumentException>(() => value["Zip"]);
        Assert.Throws<ArgumentException>(() => value["Zip"] = "0150");
        Assert.Throws<ArgumentException>(() => value.Get<string>("Zip"));

        Assert.Throws<ArgumentException>(() => value["City"] = 42);

        // An owned value inside it is one of the owned type of its field.
        value["Location"] = new LatticeOwnedValue(ValueSchema.OwnedType(schema, "Point")) { ["Lat"] = 59.9, ["Lon"] = 10.7 };
        Assert.Throws<ArgumentException>(() => value["Location"] = new LatticeOwnedValue(ValueSchema.OwnedType(schema, "Address")));

        // A collection inside it is checked as one of a record.
        value["Lines"] = new[] { "Karl Johans gate 1", "Sentrum" };
        Assert.Equal(["Karl Johans gate 1", "Sentrum"], Assert.IsType<List<string>>(value["Lines"]));
        Assert.Throws<ArgumentException>(() => value["Lines"] = new[] { "Karl Johans gate 1", null });

        // Get gives the value as the type asked for, or fails.
        Assert.Throws<InvalidCastException>(() => value.Get<int>("Lines"));
    }

    [Fact]
    public void Record_holds_an_owned_value_of_the_owned_type_of_its_field()
    {
        var schema = ValueSchema.Compile();
        var address = ValueSchema.OwnedType(schema, "Address");
        var point = ValueSchema.OwnedType(schema, "Point");

        var record = new LatticeRecord(ValueSchema.User(schema))
        {
            ["Home"] = new LatticeOwnedValue(address) { ["City"] = "Oslo" },
            ["Spot"] = new LatticeOwnedValue(point) { ["Lat"] = 59.9, ["Lon"] = 10.7 },
        };

        // Address names a class and Point does not; a record holds both as owned values.
        var home = Assert.IsType<LatticeOwnedValue>(record["Home"]);
        Assert.Same(address, home.OwnedType);
        Assert.Equal("Oslo", home["City"]);
        var spot = Assert.IsType<LatticeOwnedValue>(record["Spot"]);
        Assert.Same(point, spot.OwnedType);
        Assert.Equal(59.9, spot["Lat"]);

        // A nullable owned field may hold no value.
        record["Home"] = null;
        Assert.Null(record["Home"]);
    }

    [Fact]
    public void Record_refuses_an_owned_value_of_another_owned_type_and_any_other_value()
    {
        var schema = ValueSchema.Compile();
        var record = new LatticeRecord(ValueSchema.User(schema));

        Assert.Throws<ArgumentException>(() => record["Work"] = new LatticeOwnedValue(ValueSchema.OwnedType(schema, "Point")));
        Assert.Throws<ArgumentException>(() => record["Work"] = "Oslo");
        Assert.Throws<ArgumentException>(() => record["Work"] = new Dictionary<string, object?> { ["City"] = "Oslo" });
        Assert.Throws<ArgumentException>(() => record["Spot"] = new LatticeOwnedValue(ValueSchema.OwnedType(schema, "Address")));
    }

    [Fact]
    public void Record_holds_owned_values_inside_lists_and_maps_as_owned_values()
    {
        var schema = ValueSchema.Compile();
        var address = ValueSchema.OwnedType(schema, "Address");
        var oslo = new LatticeOwnedValue(address) { ["City"] = "Oslo" };
        var bergen = new LatticeOwnedValue(address) { ["City"] = "Bergen" };

        var record = new LatticeRecord(ValueSchema.User(schema))
        {
            ["Places"] = new[] { oslo, bergen, oslo },
            ["Sites"] = new Dictionary<int, LatticeOwnedValue> { [1] = bergen },
        };

        var places = Assert.IsType<List<LatticeOwnedValue>>(record["Places"]);
        Assert.Equal(["Oslo", "Bergen", "Oslo"], places.Select(place => place.Get<string>("City")));
        var sites = Assert.IsType<Dictionary<int, LatticeOwnedValue>>(record["Sites"]);
        Assert.Equal("Bergen", sites[1]["City"]);

        // Every owned value in them is of the owned type the declaration names, and not null.
        var point = new LatticeOwnedValue(ValueSchema.OwnedType(schema, "Point"));
        Assert.Throws<ArgumentException>(() => record["Places"] = new[] { oslo, point });
        Assert.Throws<ArgumentException>(() => record["Places"] = new[] { oslo, null });
        Assert.Throws<ArgumentException>(() => record["Sites"] = new Dictionary<int, LatticeOwnedValue?> { [1] = null });
    }
}
