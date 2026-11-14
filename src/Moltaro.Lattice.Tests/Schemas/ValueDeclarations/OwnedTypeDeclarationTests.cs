namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// Owned types: the fields of a value that belongs to the entity holding it, declared once in
// the schema and named by the fields that hold such a value. Owned types are a scope of their
// own for identities and names, as enum types and entity types are; the fields of an owned
// type are a scope of their own for field identities. A .NET class is named by at most one
// type of the schema.
public sealed class OwnedTypeDeclarationTests
{
    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Descriptor_declares_owned_types_with_their_fields_and_classes()
    {
        var schema = ValueSchema.Compile();

        Assert.Equal(["Address", "Point"], schema.OwnedTypes.Select(ownedType => ownedType.Name));

        var address = ValueSchema.OwnedType(schema, "Address");
        Assert.Equal(1, address.Id);
        Assert.Equal("Example.Address", address.ClrTypeName);
        Assert.Equal(
            [(1, "City"), (2, "Street"), (3, "Floor"), (4, "Location"), (5, "Lines"), (6, "Owner")],
            address.Fields.Select(field => (field.Id, field.Name)));

        Assert.True(address.TryGetField("Street", out var street));
        Assert.Equal(LatticeFieldTypeEnum.String, street.Declaration.Type);
        Assert.True(street.Declaration.IsNullable);
        Assert.False(address.TryGetField("Zip", out _));

        // A class is optional for an owned type, as for an entity type.
        var point = ValueSchema.OwnedType(schema, "Point");
        Assert.Equal(2, point.Id);
        Assert.Null(point.ClrTypeName);

        // An owned type is found among owned types only.
        Assert.False(schema.TryGetOwnedType("User", out _));
        Assert.False(schema.TryGetOwnedType("Color", out _));
        Assert.False(schema.TryGetOwnedType("Planet", out _));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Field_of_type_Owned_holds_a_value_of_the_owned_type_it_names()
    {
        var schema = ValueSchema.Compile();
        var address = ValueSchema.OwnedType(schema, "Address");
        var point = ValueSchema.OwnedType(schema, "Point");

        var home = ValueSchema.Field(schema, "Home").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.Owned, home.Type);
        Assert.Same(address, home.OwnedType);
        Assert.True(home.IsNullable);
        Assert.Equal(typeof(LatticeOwnedValue), home.ValueType);

        // The policies of other types are not there.
        Assert.Null(home.Comparison);
        Assert.Null(home.StringStorage);
        Assert.Null(home.EnumType);
        Assert.Null(home.ReferencedEntityTypeName);
        Assert.Null(home.Element);
        Assert.Null(home.Key);
        Assert.Null(home.Value);

        // Two fields may hold the same owned type, one of them not nullable.
        var work = ValueSchema.Field(schema, "Work").Declaration;
        Assert.Same(address, work.OwnedType);
        Assert.False(work.IsNullable);

        // An owned type holds another one.
        Assert.True(address.TryGetField("Location", out var location));
        Assert.Same(point, location.Declaration.OwnedType);
        Assert.Equal(typeof(LatticeOwnedValue), location.Declaration.ValueType);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Schema_without_owned_types_has_none()
    {
        var schema = LatticeSchema.Compile(TestData.SchemaJson);

        Assert.Empty(schema.OwnedTypes);
        Assert.False(schema.TryGetOwnedType("User", out _));
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public void Owned_types_are_a_scope_of_their_own_for_identities_and_names()
    {
        // The owned type Group has the identity and the name of the entity type Group and of
        // the enum type Group; each declaration names a type of one kind.
        var schema = LatticeSchema.Compile("""
            {
              "Enums": [ { "Id": 1, "Name": "Group", "Members": [ { "Name": "None", "Value": 0 } ] } ],
              "OwnedTypes": [
                { "Id": 1, "Name": "Group", "Fields": [ { "Id": 1, "Name": "Title", "Type": "String" } ] },
                { "Id": 2, "Name": "Badge", "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" } ] }
              ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Group",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Details", "Type": "Owned", "OwnedType": "Group" },
                    { "Id": 3, "Name": "Parent", "Type": "Reference", "EntityType": "Group", "Nullable": true },
                    { "Id": 4, "Name": "Kind", "Type": "Enum", "Enum": "Group" }
                  ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);

        Assert.True(schema.TryGetOwnedType("Group", out var ownedGroup));
        Assert.True(schema.TryGetEntityType("Group", out var entityGroup));
        Assert.True(schema.TryGetEnumType("Group", out var enumGroup));
        Assert.Equal((1, 1, 1), (ownedGroup.Id, entityGroup.Id, enumGroup.Id));

        Assert.True(entityGroup.TryGetField("Details", out var details));
        Assert.Same(ownedGroup, details.Declaration.OwnedType);
        Assert.True(entityGroup.TryGetField("Parent", out var parent));
        Assert.Equal("Group", parent.Declaration.ReferencedEntityTypeName);
        Assert.True(entityGroup.TryGetField("Kind", out var kind));
        Assert.Same(enumGroup, kind.Declaration.EnumType);

        // Every owned type has its own field identities, which may also be those of an
        // entity type.
        Assert.Equal([1], ownedGroup.Fields.Select(field => field.Id));
        Assert.True(schema.TryGetOwnedType("Badge", out var badge));
        Assert.Equal([1], badge.Fields.Select(field => field.Id));
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // An owned type: zero, negative, missing, the identity of another owned type.
    [InlineData("'Id': 1, 'Name': 'Address',", "'Id': 0, 'Name': 'Address',", "Address")]
    [InlineData("'Id': 1, 'Name': 'Address',", "'Id': -1, 'Name': 'Address',", "Address")]
    [InlineData("'Id': 1, 'Name': 'Address',", "'Name': 'Address',", "Address")]
    [InlineData("'Id': 2, 'Name': 'Point',", "'Id': 1, 'Name': 'Point',", "Point")]
    // A field of an owned type: zero, negative, missing, the identity of another field of it.
    [InlineData("{ 'Id': 1, 'Name': 'City',", "{ 'Id': 0, 'Name': 'City',", "Address", "City")]
    [InlineData("{ 'Id': 1, 'Name': 'City',", "{ 'Id': -1, 'Name': 'City',", "Address", "City")]
    [InlineData("{ 'Id': 1, 'Name': 'City',", "{ 'Name': 'City',", "Address", "City")]
    [InlineData("{ 'Id': 2, 'Name': 'Street',", "{ 'Id': 1, 'Name': 'Street',", "Address", "Street")]
    public void Identity_in_an_owned_type_that_is_zero_negative_missing_or_repeated_is_refused_with_its_place(string part, string replacement, params string[] place)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, part, replacement);

        Assert.All(place, name => Assert.Contains(name, exception.Message));
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // Two owned types with one name.
    [InlineData("'OwnedTypes': [", "'OwnedTypes': [ { 'Id': 3, 'Name': 'Point', 'Fields': [ { 'Id': 1, 'Name': 'X', 'Type': 'Int32' } ] },", "Point")]
    // Two fields of an owned type with one name.
    [InlineData("{ 'Id': 2, 'Name': 'Street',", "{ 'Id': 2, 'Name': 'City',", "Address", "City")]
    public void Name_given_twice_among_owned_types_or_their_fields_is_refused_with_its_place(string part, string replacement, params string[] place)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, part, replacement);

        Assert.All(place, name => Assert.Contains(name, exception.Message));
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // Two owned types.
    [InlineData("'Id': 2, 'Name': 'Point',", "'Id': 2, 'Name': 'Point', 'ClrType': 'Example.Address',", "Example.Address")]
    // An owned type and an entity type.
    [InlineData("'Id': 2, 'Name': 'Point',", "'Id': 2, 'Name': 'Point', 'ClrType': 'Example.User',", "Example.User")]
    public void Dotnet_class_named_by_more_than_one_type_is_refused_with_the_class_named(string part, string replacement, string className)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, part, replacement);

        Assert.Contains(className, exception.Message);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // A field of type Owned that names no owned type, one the schema does not declare, or an
    // entity type, which is not an owned type.
    [InlineData("{ 'Id': 3, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Address' }", "{ 'Id': 3, 'Name': 'Work', 'Type': 'Owned' }", "User", "Work")]
    [InlineData("{ 'Id': 3, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Address' }", "{ 'Id': 3, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Planet' }", "User", "Work")]
    [InlineData("{ 'Id': 3, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Address' }", "{ 'Id': 3, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Group' }", "User", "Work")]
    // The same inside an owned type and as an element.
    [InlineData("'Name': 'Location', 'Type': 'Owned', 'OwnedType': 'Point'", "'Name': 'Location', 'Type': 'Owned', 'OwnedType': 'Planet'", "Address", "Location")]
    [InlineData("'Element': { 'Type': 'Owned', 'OwnedType': 'Address' }", "'Element': { 'Type': 'Owned' }", "User", "Places")]
    // An owned type named by a field that is not of type Owned, or by a List rather than by
    // its element.
    [InlineData("{ 'Id': 20, 'Name': 'Login', 'Type': 'String' }", "{ 'Id': 20, 'Name': 'Login', 'Type': 'String', 'OwnedType': 'Address' }", "User", "Login")]
    [InlineData("'Name': 'Places', 'Type': 'List',", "'Name': 'Places', 'Type': 'List', 'OwnedType': 'Address',", "User", "Places")]
    public void Owned_value_names_a_declared_owned_type_and_nothing_else_names_one(string part, string replacement, params string[] place)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, part, replacement);

        Assert.All(place, name => Assert.Contains(name, exception.Message));
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    // An owned type has no key and no indexes of its own.
    [InlineData("'Id': 2, 'Name': 'Point',", "'Id': 2, 'Name': 'Point', 'PrimaryKey': [1],")]
    [InlineData("'Id': 2, 'Name': 'Point',", "'Id': 2, 'Name': 'Point', 'Indexes': [],")]
    [InlineData("'Id': 2, 'Name': 'Point',", "'Id': 2, 'Name': 'Point', 'Comment': 'not part of the form',")]
    // A property given twice.
    [InlineData("'Id': 2, 'Name': 'Point',", "'Id': 2, 'Name': 'Point', 'Name': 'Spot',")]
    // A field of an owned type is closed as a field of an entity type is.
    [InlineData("{ 'Id': 1, 'Name': 'Lat', 'Type': 'Float64' }", "{ 'Id': 1, 'Name': 'Lat', 'Type': 'Float64', 'Default': 0 }")]
    [InlineData("{ 'Id': 1, 'Name': 'Lat', 'Type': 'Float64' }", "{ 'Id': 1, 'Name': 'Lat', 'Type': 'Float64', 'Comparison': 'Ordinal' }")]
    public void Owned_type_has_the_properties_of_its_form_once_and_no_others(string part, string replacement)
    {
        Descriptor.ExpectRefused(ValueSchema.Json, part, replacement);
    }
}
