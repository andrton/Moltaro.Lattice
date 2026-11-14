namespace Moltaro.Lattice.Tests;

// A part of an index may be a field inside an owned value. In the descriptor such a part is a
// list of field identities, from a field of the entity type down to the indexed field, in
// place of one identity. Every step of the path but the last is an Owned field, and the last
// is a field an index may have; a path never passes through a List, a Map or a Set. A search
// key may give null for a part exactly when a value of that part can be null: the indexed
// field is nullable, or an owned value on its path is.
public sealed class IndexPathTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Index_part_is_a_path_from_a_field_of_the_entity_type_to_the_indexed_field()
    {
        var schema = ValueSchema.Compile();
        var user = ValueSchema.User(schema);
        var address = ValueSchema.OwnedType(schema, "Address");
        var point = ValueSchema.OwnedType(schema, "Point");
        Assert.True(address.TryGetField("City", out var city));
        Assert.True(address.TryGetField("Location", out var location));
        Assert.True(address.TryGetField("Floor", out var floor));
        Assert.True(point.TryGetField("Lat", out var lat));

        // Home.City: two steps.
        Assert.True(user.TryGetIndex("ByHomeCity", out var byHomeCity));
        var homeCity = Assert.Single(byHomeCity.Parts);
        Assert.Equal([ValueSchema.Field(schema, "Home"), city], homeCity.Path);
        Assert.Same(city, homeCity.Field);

        // Home.Location.Lat: through two owned values.
        Assert.True(user.TryGetIndex("ByHomeLat", out var byHomeLat));
        var homeLat = Assert.Single(byHomeLat.Parts);
        Assert.Equal([ValueSchema.Field(schema, "Home"), location, lat], homeLat.Path);
        Assert.Same(lat, homeLat.Field);

        // A field of the entity type and a path, in the order of the descriptor.
        Assert.True(user.TryGetIndex("ByLoginAndWorkFloor", out var byLoginAndWorkFloor));
        Assert.Equal(2, byLoginAndWorkFloor.Parts.Count);
        Assert.Equal([ValueSchema.Field(schema, "Login")], byLoginAndWorkFloor.Parts[0].Path);
        Assert.Same(ValueSchema.Field(schema, "Login"), byLoginAndWorkFloor.Parts[0].Field);
        Assert.Equal([ValueSchema.Field(schema, "Work"), floor], byLoginAndWorkFloor.Parts[1].Path);
    }

    [Fact]
    public void Index_part_of_a_field_of_the_entity_type_is_a_path_of_that_field_alone()
    {
        var schema = LatticeSchema.Compile(TestData.SchemaJson);
        Assert.True(schema.TryGetEntityType("User", out var user));
        Assert.True(user.TryGetIndex("ByAgeAndName", out var byAgeAndName));

        Assert.All(byAgeAndName.Parts, part => Assert.Same(part.Field, Assert.Single(part.Path)));
        Assert.Equal(["Age", "Name"], byAgeAndName.Parts.Select(part => part.Field.Name));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Index_may_cover_one_field_of_an_owned_type_through_two_owned_values()
    {
        // Home.City and Work.City are the same field of Address in two owned values.
        var json = Descriptor.With(ValueSchema.Json, "'Fields': [ [2, 1] ] }", "'Fields': [ [2, 1], [3, 1] ] }");

        var user = ValueSchema.User(LatticeSchema.Compile(json));

        Assert.True(user.TryGetIndex("ByHomeCity", out var index));
        Assert.Equal(["Home", "Work"], index.Parts.Select(part => part.Path[0].Name));
        Assert.Same(index.Parts[0].Field, index.Parts[1].Field);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // A step that is not an Owned field: a scalar, a reference, a List, a Map, a Set.
    [InlineData("[ [20, 1] ]")]
    [InlineData("[ [5, 1] ]")]
    [InlineData("[ [10, 1] ]")]
    [InlineData("[ [11, 1] ]")]
    [InlineData("[ [17, 1] ]")]
    // A path through a List inside an owned value.
    [InlineData("[ [2, 5, 1] ]")]
    // A field that the entity type or the owned type does not have.
    [InlineData("[ [99, 1] ]")]
    [InlineData("[ [2, 99] ]")]
    [InlineData("[ [2, 4, 99] ]")]
    public void Path_through_a_field_that_is_not_an_owned_value_or_to_a_field_that_does_not_exist_is_refused_with_its_place(string fields)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, "'Fields': [ [2, 1] ] }", $"'Fields': {fields} }}");

        Assert.Contains("User", exception.Message);
        Assert.Contains("ByHomeCity", exception.Message);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Path_to_a_field_that_an_index_cannot_have_is_refused_with_its_place()
    {
        // A Binary field inside an owned value.
        var json = Descriptor.With(ValueSchema.Json, "{ 'Id': 2, 'Name': 'Lon', 'Type': 'Float64' }", "{ 'Id': 2, 'Name': 'Lon', 'Type': 'Float64' }, { 'Id': 3, 'Name': 'Map', 'Type': 'Binary', 'Nullable': true }");

        var exception = Descriptor.ExpectRefused(json, "'Fields': [ [2, 4, 1] ] }", "'Fields': [ [2, 4, 3] ] }");

        Assert.Contains("User", exception.Message);
        Assert.Contains("ByHomeLat", exception.Message);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // An element of Fields is an identity or a list of identities.
    [InlineData("[ ['2', 1] ]")]
    [InlineData("[ { 'Path': [2, 1] } ]")]
    [InlineData("[ [2, [4, 1]] ]")]
    public void Index_part_that_is_neither_an_identity_nor_a_list_of_identities_is_refused(string fields)
    {
        Descriptor.ExpectRefused(ValueSchema.Json, "'Fields': [ [2, 1] ] }", $"'Fields': {fields} }}");
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Search_key_may_give_null_for_a_part_that_can_be_null_and_the_search_is_not_implemented_yet()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SearchSchema());

        foreach (var index in new[] { "ByHomeCity", "ByWorkStreet", "ByWorkLocationLat" })
        {
            // Home is nullable; Street is nullable; Location is nullable. The key is accepted,
            // and the search, which is not implemented, is refused after it.
            var byRecord = database.Set("Person").Index(index);
            var byClass = database.Set<Person>().Index(index);

            Assert.Throws<NotSupportedException>(() => byRecord.Find(IndexKey.Equal<object?>(null)).ToList());
            Assert.Throws<NotSupportedException>(() => byClass.Find(IndexKey.Equal<object?>(null)).ToList());
            await Assert.ThrowsAsync<NotSupportedException>(async () => await byRecord.FindAsync(IndexKey.Equal<object?>(null), new LatticeFindOptions()));
            await Assert.ThrowsAsync<NotSupportedException>(async () => await byClass.FindAsync(IndexKey.Equal<object?>(null), new LatticeFindOptions()));
        }
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Search_key_that_gives_null_for_a_part_that_is_never_null_is_refused_before_the_search()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SearchSchema());

        // Work and Work.City are not nullable, so Work.City always has a value.
        var byRecord = database.Set("Person").Index("ByWorkCity");
        var byClass = database.Set<Person>().Index("ByWorkCity");

        Assert.Throws<ArgumentException>(() => byRecord.Find(IndexKey.Equal<string?>(null)).ToList());
        Assert.Throws<ArgumentException>(() => byClass.Find(IndexKey.Equal<string?>(null)).ToList());
        await Assert.ThrowsAsync<ArgumentException>(async () => await byRecord.FindAsync(IndexKey.Equal<string?>(null), new LatticeFindOptions()));
        await Assert.ThrowsAsync<ArgumentException>(async () => await byClass.FindAsync(IndexKey.Equal<string?>(null), new LatticeFindOptions()));

        // A value is checked against the indexed field, as for a field of the entity type.
        Assert.Throws<NotSupportedException>(() => byRecord.Find(IndexKey.Equal("Oslo")).ToList());
        Assert.Throws<ArgumentException>(() => byRecord.Find(IndexKey.Equal(42)).ToList());
    }

    // Person has a nullable owned value Home and one that is not, Work; Address has a field
    // that is not nullable, City, one that is, Street, and a nullable owned value Location.
    private static LatticeSchema SearchSchema() => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "{{typeof(Address).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Street", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "Location", "Type": "Owned", "OwnedType": "Point", "Nullable": true }
              ]
            },
            {
              "Id": 2, "Name": "Point", "ClrType": "{{typeof(Point).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Lat", "Type": "Float64" } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Person", "ClrType": "{{typeof(Person).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 3, "Name": "Work", "Type": "Owned", "OwnedType": "Address" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByHomeCity", "Fields": [ [2, 1] ] },
                { "Id": 2, "Name": "ByWorkCity", "Fields": [ [3, 1] ] },
                { "Id": 3, "Name": "ByWorkStreet", "Fields": [ [3, 2] ] },
                { "Id": 4, "Name": "ByWorkLocationLat", "Fields": [ [3, 3, 1] ] }
              ]
            }
          ]
        }
        """);

    private sealed class Person
    {
        public long Id { get; set; }

        public Address? Home { get; set; }

        public Address Work { get; set; } = new();
    }

    private sealed class Address
    {
        public string City { get; set; } = "";

        public string? Street { get; set; }

        public Point? Location { get; set; }
    }

    private sealed class Point
    {
        public double Lat { get; set; }
    }
}
