namespace Moltaro.Lattice.Tests.NestedValues.Owned;

// An index over a field inside an owned value finds the entity by that field; when the owned value is absent
// the component is null, the entity is still found by a search for null, and a unique index does not compare
// it (LatticeIndexPart; docs/decisions/index-access.md). The same holds on a child, whose indexes
// are its own.
//
// The schema: Member, whose key Code is a String, holds Home, a nullable owned Address, and Work, one that is
// not; an Address holds City, a String, Zip, an Int32, and Geo, a nullable owned Point of Lat and Lon. The
// indexes: ByHomeCity over Home.City, ByWorkCity over Work.City, ByHomeLat over Home.Geo.Lat, through two
// owned values, and ByHomeZip, unique, over Home.Zip.
public sealed class OwnedPathIndexTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Zip", "Type": "Int32" },
                { "Id": 3, "Name": "Geo", "Type": "Owned", "OwnedType": "Point", "Nullable": true }
              ]
            },
            { "Id": 2, "Name": "Point", "Fields": [ { "Id": 1, "Name": "Lat", "Type": "Float64" }, { "Id": 2, "Name": "Lon", "Type": "Float64" } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Member",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 3, "Name": "Work", "Type": "Owned", "OwnedType": "Address" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByHomeCity", "Fields": [ [2, 1] ] },
                { "Id": 2, "Name": "ByWorkCity", "Fields": [ [3, 1] ] },
                { "Id": 3, "Name": "ByHomeLat", "Fields": [ [2, 3, 1] ] },
                { "Id": 4, "Name": "ByHomeZip", "Fields": [ [2, 2] ], "Unique": true }
              ]
            }
          ]
        }
        """);

    [Fact]
    public async Task Index_over_a_field_inside_an_owned_value_finds_the_entity_by_that_field()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var members = database.Set("Member");

        await members.InsertAsync(MemberOf(members, "ada", home: ("Oslo", 150, 59.9), work: ("Bergen", 5003, null)));
        await members.InsertAsync(MemberOf(members, "alan", home: ("Bergen", 5004, null), work: ("Oslo", 151, 59.9)));
        await members.InsertAsync(MemberOf(members, "grace", home: ("Oslo", 152, 60.1), work: ("Oslo", 152, null)));

        Assert.Equal(["ada", "grace"], CodesOf(members.Index("ByHomeCity").Find(IndexKey.Equal("Oslo"))));
        Assert.Equal(["alan", "grace"], CodesOf(members.Index("ByWorkCity").Find(IndexKey.Equal("Oslo"))));
        Assert.Equal(["ada"], CodesOf(members.Index("ByHomeLat").Find(IndexKey.Equal(59.9))));
        Assert.Equal(["grace"], CodesOf(members.Index("ByHomeZip").Find(IndexKey.Equal(152))));
        Assert.Equal(["ada", "grace"], CodesOf((await members.Index("ByHomeCity").FindAsync(IndexKey.Equal("Oslo"), new LatticeFindOptions())).Items));
    }

    [Fact]
    public async Task Entity_whose_owned_value_is_absent_is_found_by_a_search_for_null()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var members = database.Set("Member");

        await members.InsertAsync(MemberOf(members, "homeless", home: null, work: ("Oslo", 150, null)));
        await members.InsertAsync(MemberOf(members, "without geo", home: ("Oslo", 151, null), work: ("Oslo", 151, null)));
        await members.InsertAsync(MemberOf(members, "with geo", home: ("Oslo", 152, 59.9), work: ("Oslo", 152, null)));

        // Home absent: every part over Home is null. Geo absent inside a present Home: only Home.Geo.Lat is.
        Assert.Equal(["homeless"], CodesOf(members.Index("ByHomeCity").Find(IndexKey.Equal<string?>(null))));
        Assert.Equal(["homeless"], CodesOf(members.Index("ByHomeZip").Find(IndexKey.Equal<int?>(null))));
        Assert.Equal(["homeless", "without geo"], CodesOf(members.Index("ByHomeLat").Find(IndexKey.Equal<double?>(null))));
        Assert.Equal(["with geo"], CodesOf(members.Index("ByHomeLat").Find(IndexKey.Equal(59.9))));
        Assert.Equal(["with geo", "without geo"], CodesOf(members.Index("ByHomeCity").Find(IndexKey.Equal("Oslo"))));
    }

    [Fact]
    public async Task Unique_index_over_a_path_does_not_compare_entities_whose_owned_value_is_absent()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var members = database.Set("Member");

        // Three entities without Home: none of them is compared under ByHomeZip.
        await members.InsertAsync(MemberOf(members, "first", home: null, work: ("Oslo", 150, null)));
        await members.InsertAsync(MemberOf(members, "second", home: null, work: ("Oslo", 150, null)));
        await members.InsertAsync(MemberOf(members, "third", home: ("Oslo", 150, null), work: ("Oslo", 150, null)));
        await members.UpsertAsync(MemberOf(members, "second", home: null, work: ("Bergen", 5003, null)));

        // A present Home with a Zip another entity holds is compared, and refused.
        var refused = await TestData.ExpectErrorAsync(
            LatticeErrorEnum.UniqueConstraintViolation,
            () => members.InsertAsync(MemberOf(members, "fourth", home: ("Bergen", 150, null), work: ("Oslo", 150, null))));
        Assert.Contains("ByHomeZip", refused.Message, StringComparison.Ordinal);

        // Once the holder of 150 has no Home, the value is free.
        await members.ReplaceAsync(MemberOf(members, "third", home: null, work: ("Oslo", 150, null)));
        await members.InsertAsync(MemberOf(members, "fourth", home: ("Bergen", 150, null), work: ("Oslo", 150, null)));

        Assert.Equal(["first", "second", "third"], CodesOf(members.Index("ByHomeZip").Find(IndexKey.Equal<int?>(null))));
        Assert.Equal(["fourth"], CodesOf(members.Index("ByHomeZip").Find(IndexKey.Equal(150))));
    }

    [Fact]
    public async Task Owned_value_that_appears_and_disappears_moves_the_entity_between_its_value_and_null()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var members = database.Set("Member");
        var byHomeCity = members.Index("ByHomeCity");

        await members.InsertAsync(MemberOf(members, "mover", home: null, work: ("Oslo", 150, null)));

        foreach (var city in new[] { "Oslo", null, "Bergen", "Oslo", null, null, "Bergen" })
        {
            var home = city is null ? ((string, int, double?)?)null : (city, 150, 59.9);

            await members.ReplaceAsync(MemberOf(members, "mover", home, work: ("Oslo", 150, null)));

            Assert.Equal(MoverIf(city is null), CodesOf(byHomeCity.Find(IndexKey.Equal<string?>(null))));
            Assert.Equal(MoverIf(city == "Oslo"), CodesOf(byHomeCity.Find(IndexKey.Equal("Oslo"))));
            Assert.Equal(MoverIf(city == "Bergen"), CodesOf(byHomeCity.Find(IndexKey.Equal("Bergen"))));
        }
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Child_finds_by_a_path_and_by_null_as_its_master_does_and_follows_its_changes()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(Schema);
        var members = master.Set("Member");

        await members.InsertAsync(MemberOf(members, "loaded", home: ("Oslo", 150, 59.9), work: ("Oslo", 150, null)));
        await members.InsertAsync(MemberOf(members, "loaded without home", home: null, work: ("Oslo", 151, null)));

        await using var child = await TestDatabases.OpenChildAsync(master);
        var childMembers = child.Set("Member");

        Assert.Equal(["loaded"], CodesOf(childMembers.Index("ByHomeCity").Find(IndexKey.Equal("Oslo"))));
        Assert.Equal(["loaded without home"], CodesOf(childMembers.Index("ByHomeZip").Find(IndexKey.Equal<int?>(null))));

        // Applied after the load: the owned value disappears from one and appears in the other.
        await members.ReplaceAsync(MemberOf(members, "loaded", home: null, work: ("Oslo", 150, null)));
        var last = await members.ReplaceAsync(MemberOf(members, "loaded without home", home: ("Oslo", 151, 60.0), work: ("Oslo", 151, null)));

        await child.WaitForPositionAsync(last.Position, TimeSpan.FromMinutes(1));

        Assert.Equal(["loaded without home"], CodesOf(childMembers.Index("ByHomeCity").Find(IndexKey.Equal("Oslo"))));
        Assert.Equal(["loaded"], CodesOf(childMembers.Index("ByHomeCity").Find(IndexKey.Equal<string?>(null))));
        Assert.Equal(["loaded"], CodesOf(childMembers.Index("ByHomeLat").Find(IndexKey.Equal<double?>(null))));
        Assert.Equal(["loaded without home"], CodesOf(childMembers.Index("ByHomeZip").Find(IndexKey.Equal(151))));

        // A unique value is refused through the child as on the master.
        await TestData.ExpectErrorAsync(
            LatticeErrorEnum.UniqueConstraintViolation,
            () => childMembers.InsertAsync(MemberOf(childMembers, "taken", home: ("Bodø", 151, null), work: ("Oslo", 1, null))));
    }

    // A member with its key, Home absent or with its City, Zip and Geo.Lat, Geo absent for a Lat of null, and
    // Work in the same way.
    private static LatticeRecord MemberOf(ILatticeSet members, string code, (string City, int Zip, double? Lat)? home, (string City, int Zip, double? Lat) work)
    {
        Assert.True(members.EntityType.TryGetField("Home", out var homeField));

        var address = homeField.Declaration.OwnedType!;

        return new LatticeRecord(members.EntityType)
        {
            ["Code"] = code,
            ["Home"] = home is { } value ? AddressOf(address, value) : null,
            ["Work"] = AddressOf(address, work),
        };
    }

    private static LatticeOwnedValue AddressOf(LatticeOwnedType address, (string City, int Zip, double? Lat) value)
    {
        Assert.True(address.TryGetField("Geo", out var geo));

        var point = value.Lat is { } lat ? new LatticeOwnedValue(geo.Declaration.OwnedType!) { ["Lat"] = lat, ["Lon"] = 10.0 } : null;

        return new LatticeOwnedValue(address) { ["City"] = value.City, ["Zip"] = value.Zip, ["Geo"] = point };
    }

    private static string[] MoverIf(bool found) => found ? ["mover"] : [];

    private static string[] CodesOf(IEnumerable<LatticeRecord> found) => [.. found.Select(record => record.Get<string>("Code")!).Order(StringComparer.Ordinal)];
}
