namespace Moltaro.Lattice.Tests.Schemas.ValueDeclarations;

// Lists, Maps and Sets: a collection declares what each of its positions holds, with the
// declaration a field has except an identity and a name, and its own nullability at every
// position. A List element and a Map value may be anything a field may be, a collection
// included. A Map key and a Set element are a scalar other than Binary, an Enum or a
// Reference; a Map key is never nullable, a Set element may be.
public sealed class CollectionDeclarationTests
{
    [Fact]
    public void Descriptor_declares_lists_maps_and_sets_with_their_elements_keys_and_values()
    {
        var schema = ValueSchema.Compile();
        Assert.True(schema.TryGetEnumType("Color", out var color));
        var address = ValueSchema.OwnedType(schema, "Address");

        // A List of references.
        var groups = ValueSchema.Field(schema, "Groups").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.List, groups.Type);
        Assert.False(groups.IsNullable);
        Assert.Equal(LatticeFieldTypeEnum.Reference, groups.Element!.Type);
        Assert.Equal("Group", groups.Element.ReferencedEntityTypeName);
        Assert.False(groups.Element.IsNullable);
        Assert.Null(groups.Key);
        Assert.Null(groups.Value);

        // A Map whose key has the default text policies and whose value may be null.
        var options = ValueSchema.Field(schema, "Options").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.Map, options.Type);
        Assert.Null(options.Element);
        Assert.Equal(LatticeFieldTypeEnum.String, options.Key!.Type);
        Assert.Equal(LatticeStringComparisonEnum.Ordinal, options.Key.Comparison);
        Assert.Equal(LatticeStringStorageEnum.Plain, options.Key.StringStorage);
        Assert.False(options.Key.IsNullable);
        Assert.Equal(LatticeFieldTypeEnum.String, options.Value!.Type);
        Assert.True(options.Value.IsNullable);

        // A Set whose element states its text policies, and one whose element may be null.
        var rights = ValueSchema.Field(schema, "Rights").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.Set, rights.Type);
        Assert.Equal(LatticeStringStorageEnum.Dictionary, rights.Element!.StringStorage);
        Assert.Equal(LatticeStringComparisonEnum.Ordinal, rights.Element.Comparison);
        var aliases = ValueSchema.Field(schema, "Aliases").Declaration;
        Assert.Equal(LatticeStringComparisonEnum.OrdinalIgnoreCase, aliases.Element!.Comparison);
        Assert.True(aliases.Element.IsNullable);

        // A collection that may itself be null.
        var teams = ValueSchema.Field(schema, "Teams").Declaration;
        Assert.True(teams.IsNullable);
        Assert.False(teams.Element!.IsNullable);

        // Elements, keys and values of an enum type and of an owned type.
        Assert.Same(color, ValueSchema.Field(schema, "Paints").Declaration.Element!.EnumType);
        Assert.Same(color, ValueSchema.Field(schema, "PaintCounts").Declaration.Key!.EnumType);
        Assert.Same(address, ValueSchema.Field(schema, "Places").Declaration.Element!.OwnedType);
        Assert.Same(address, ValueSchema.Field(schema, "Sites").Declaration.Value!.OwnedType);
    }

    [Fact]
    public void Collection_may_hold_collections()
    {
        var schema = ValueSchema.Compile();

        // A List of Lists.
        var layers = ValueSchema.Field(schema, "Layers").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.List, layers.Element!.Type);
        Assert.Equal(LatticeFieldTypeEnum.Int64, layers.Element.Element!.Type);

        // A Map of Lists of references.
        var members = ValueSchema.Field(schema, "Members").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.List, members.Value!.Type);
        Assert.Equal("Group", members.Value.Element!.ReferencedEntityTypeName);

        // A Map of Maps of Sets.
        var grid = ValueSchema.Field(schema, "Grid").Declaration;
        Assert.Equal(LatticeFieldTypeEnum.Guid, grid.Key!.Type);
        Assert.Equal(LatticeFieldTypeEnum.Map, grid.Value!.Type);
        Assert.Equal(LatticeFieldTypeEnum.Int32, grid.Value.Key!.Type);
        Assert.Equal(LatticeFieldTypeEnum.Set, grid.Value.Value!.Type);
        Assert.Equal(LatticeFieldTypeEnum.DateOnly, grid.Value.Value.Element!.Type);
    }

    [Fact]
    public void Declaration_that_is_not_a_collection_has_no_element_key_or_value()
    {
        var schema = ValueSchema.Compile();

        foreach (var name in new[] { "Id", "Login", "Home", "Manager" })
        {
            var declaration = ValueSchema.Field(schema, name).Declaration;
            Assert.Null(declaration.Element);
            Assert.Null(declaration.Key);
            Assert.Null(declaration.Value);
        }

        // A scalar names no owned type and no entity type either.
        var login = ValueSchema.Field(schema, "Login").Declaration;
        Assert.Null(login.OwnedType);
        Assert.Null(login.ReferencedEntityTypeName);
    }

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Int8")]
    [InlineData("UInt64")]
    [InlineData("Float32")]
    [InlineData("Decimal")]
    [InlineData("String")]
    [InlineData("Guid")]
    [InlineData("DateTimeOffset")]
    [InlineData("TimeSpan")]
    [InlineData("Binary")]
    [InlineData("Enum")]
    [InlineData("Owned")]
    [InlineData("Reference")]
    [InlineData("List")]
    [InlineData("Map")]
    [InlineData("Set")]
    public void List_element_and_Map_value_may_be_of_any_type(string type)
    {
        var schema = LatticeSchema.Compile(Holder($$"""
            { 'Id': 2, 'Name': 'Items', 'Type': 'List', 'Element': {{Declaration(type)}} },
            { 'Id': 3, 'Name': 'ItemsByName', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': {{Declaration(type)}} }
            """));

        var holder = schema.EntityTypes[0];
        Assert.True(holder.TryGetField("Items", out var items));
        Assert.Equal(Enum.Parse<LatticeFieldTypeEnum>(type), items.Declaration.Element!.Type);
        Assert.True(holder.TryGetField("ItemsByName", out var itemsByName));
        Assert.Equal(Enum.Parse<LatticeFieldTypeEnum>(type), itemsByName.Declaration.Value!.Type);
    }

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Int8")]
    [InlineData("UInt8")]
    [InlineData("Int16")]
    [InlineData("UInt16")]
    [InlineData("Int32")]
    [InlineData("UInt32")]
    [InlineData("Int64")]
    [InlineData("UInt64")]
    [InlineData("Float32")]
    [InlineData("Float64")]
    [InlineData("Decimal")]
    [InlineData("String")]
    [InlineData("Guid")]
    [InlineData("DateTimeOffset")]
    [InlineData("DateOnly")]
    [InlineData("TimeOnly")]
    [InlineData("TimeSpan")]
    [InlineData("Enum")]
    [InlineData("Reference")]
    public void Map_key_and_Set_element_may_be_a_scalar_other_than_Binary_an_Enum_or_a_Reference(string type)
    {
        var schema = LatticeSchema.Compile(Holder($$"""
            { 'Id': 2, 'Name': 'Counts', 'Type': 'Map', 'Key': {{Declaration(type)}}, 'Value': { 'Type': 'Int32' } },
            { 'Id': 3, 'Name': 'Distinct', 'Type': 'Set', 'Element': {{Declaration(type)}} }
            """));

        var holder = schema.EntityTypes[0];
        Assert.True(holder.TryGetField("Counts", out var counts));
        Assert.Equal(Enum.Parse<LatticeFieldTypeEnum>(type), counts.Declaration.Key!.Type);
        Assert.True(holder.TryGetField("Distinct", out var distinct));
        Assert.Equal(Enum.Parse<LatticeFieldTypeEnum>(type), distinct.Declaration.Element!.Type);
    }

    [Theory]
    [InlineData("Binary")]
    [InlineData("Owned")]
    [InlineData("List")]
    [InlineData("Map")]
    [InlineData("Set")]
    public void Map_key_that_is_complex_or_Binary_is_refused_with_its_place(string type)
    {
        // The same Map with a key of Int32 compiles.
        LatticeSchema.Compile(Holder("{ 'Id': 2, 'Name': 'Counts', 'Type': 'Map', 'Key': { 'Type': 'Int32' }, 'Value': { 'Type': 'Int32' } }"));

        var json = Holder($"{{ 'Id': 2, 'Name': 'Counts', 'Type': 'Map', 'Key': {Declaration(type)}, 'Value': {{ 'Type': 'Int32' }} }}");

        ExpectRefusedWithPlace(json, "Holder", "Counts");
    }

    [Theory]
    [InlineData("Binary")]
    [InlineData("Owned")]
    [InlineData("List")]
    [InlineData("Map")]
    [InlineData("Set")]
    public void Set_element_that_is_complex_or_Binary_is_refused_with_its_place(string type)
    {
        LatticeSchema.Compile(Holder("{ 'Id': 2, 'Name': 'Distinct', 'Type': 'Set', 'Element': { 'Type': 'Int32' } }"));

        var json = Holder($"{{ 'Id': 2, 'Name': 'Distinct', 'Type': 'Set', 'Element': {Declaration(type)} }}");

        ExpectRefusedWithPlace(json, "Holder", "Distinct");
    }

    [Fact]
    public void Complex_key_or_Set_element_inside_another_collection_is_refused_with_its_place()
    {
        // The Map and the Set are values of a Map, which may hold anything.
        LatticeSchema.Compile(Holder("{ 'Id': 2, 'Name': 'Nested', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Set', 'Element': { 'Type': 'Int32' } } }"));

        ExpectRefusedWithPlace(
            Holder("{ 'Id': 2, 'Name': 'Nested', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Set', 'Element': { 'Type': 'Owned', 'OwnedType': 'Address' } } }"),
            "Holder",
            "Nested");
        ExpectRefusedWithPlace(
            Holder("{ 'Id': 2, 'Name': 'Nested', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Map', 'Key': { 'Type': 'Binary' }, 'Value': { 'Type': 'Int32' } } }"),
            "Holder",
            "Nested");
    }

    [Fact]
    public void Nullable_Map_key_is_refused_with_its_place_and_a_nullable_Set_element_is_not()
    {
        // A key that states it is not nullable is a key as before.
        LatticeSchema.Compile(Descriptor.With(ValueSchema.Json, "'Key': { 'Type': 'String' }, 'Value': { 'Type': 'String', 'Nullable': true }", "'Key': { 'Type': 'String', 'Nullable': false }, 'Value': { 'Type': 'String', 'Nullable': true }"));

        var exception = Descriptor.ExpectRefused(ValueSchema.Json, "'Key': { 'Type': 'String' }, 'Value': { 'Type': 'String', 'Nullable': true }", "'Key': { 'Type': 'String', 'Nullable': true }, 'Value': { 'Type': 'String', 'Nullable': true }");
        Assert.Contains("User", exception.Message);
        Assert.Contains("Options", exception.Message);

        // A nullable Set element allows one null member.
        Assert.True(ValueSchema.Field(ValueSchema.Compile(), "Aliases").Declaration.Element!.IsNullable);
    }

    [Theory]
    // A collection without what its kind needs.
    [InlineData("{ 'Id': 6, 'Name': 'Groups', 'Type': 'List', 'Element': { 'Type': 'Reference', 'EntityType': 'Group' } }", "{ 'Id': 6, 'Name': 'Groups', 'Type': 'List' }", "User", "Groups")]
    [InlineData("'Name': 'Rights', 'Type': 'Set', 'Element': { 'Type': 'String', 'StringStorage': 'Dictionary' }", "'Name': 'Rights', 'Type': 'Set'", "User", "Rights")]
    [InlineData("'Name': 'Options', 'Type': 'Map', 'Key': { 'Type': 'String' },", "'Name': 'Options', 'Type': 'Map',", "User", "Options")]
    [InlineData("'Key': { 'Type': 'String' }, 'Value': { 'Type': 'String', 'Nullable': true }", "'Key': { 'Type': 'String' }", "User", "Options")]
    // What another kind has: a key or a value for a List or a Set, an element for a Map, an
    // element for a field that is not a collection.
    [InlineData("'Name': 'Groups', 'Type': 'List',", "'Name': 'Groups', 'Type': 'List', 'Key': { 'Type': 'Int32' },", "User", "Groups")]
    [InlineData("'Name': 'Rights', 'Type': 'Set',", "'Name': 'Rights', 'Type': 'Set', 'Value': { 'Type': 'Int32' },", "User", "Rights")]
    [InlineData("'Name': 'Options', 'Type': 'Map',", "'Name': 'Options', 'Type': 'Map', 'Element': { 'Type': 'Int32' },", "User", "Options")]
    [InlineData("{ 'Id': 20, 'Name': 'Login', 'Type': 'String' }", "{ 'Id': 20, 'Name': 'Login', 'Type': 'String', 'Element': { 'Type': 'String' } }", "User", "Login")]
    // A text policy or an enum type where the inner type has none, at every depth.
    [InlineData("'Element': { 'Type': 'Int32', 'Nullable': true }", "'Element': { 'Type': 'Int32', 'Nullable': true, 'Comparison': 'Ordinal' }", "User", "Scores")]
    [InlineData("'Element': { 'Type': 'List', 'Element': { 'Type': 'Int64' } }", "'Element': { 'Type': 'List', 'Element': { 'Type': 'Int64', 'StringStorage': 'Plain' } }", "User", "Layers")]
    [InlineData("'Key': { 'Type': 'Int32' }, 'Value': { 'Type': 'Owned', 'OwnedType': 'Address' }", "'Key': { 'Type': 'Int32', 'Enum': 'Color' }, 'Value': { 'Type': 'Owned', 'OwnedType': 'Address' }", "User", "Sites")]
    // An Enum element that names no enum type, an element of an unknown type.
    [InlineData("'Element': { 'Type': 'Enum', 'Enum': 'Color' }", "'Element': { 'Type': 'Enum' }", "User", "Paints")]
    [InlineData("'Element': { 'Type': 'Binary' }", "'Element': { 'Type': 'Blob' }", "User", "Blobs")]
    public void Collection_declares_what_its_kind_has_and_nothing_else(string part, string replacement, params string[] place)
    {
        var exception = Descriptor.ExpectRefused(ValueSchema.Json, part, replacement);

        Assert.All(place, name => Assert.Contains(name, exception.Message));
    }

    [Theory]
    // An element, a key and a value take what a field takes, except an identity and a name.
    [InlineData("'Element': { 'Type': 'Binary' }", "'Element': { 'Id': 1, 'Type': 'Binary' }")]
    [InlineData("'Element': { 'Type': 'Binary' }", "'Element': { 'Name': 'Blob', 'Type': 'Binary' }")]
    [InlineData("'Key': { 'Type': 'Guid' }", "'Key': { 'Id': 1, 'Type': 'Guid' }")]
    [InlineData("'Value': { 'Type': 'Set', 'Element': { 'Type': 'DateOnly' } }", "'Value': { 'Name': 'Days', 'Type': 'Set', 'Element': { 'Type': 'DateOnly' } }")]
    // A property the form does not have, and one given twice.
    [InlineData("'Element': { 'Type': 'Binary' }", "'Element': { 'Type': 'Binary', 'MaxLength': 16 }")]
    [InlineData("'Element': { 'Type': 'Binary' }", "'Element': { 'Type': 'Binary', 'Type': 'String' }")]
    // An element is a declaration, not a type name.
    [InlineData("'Element': { 'Type': 'Binary' }", "'Element': 'Binary'")]
    public void Element_key_and_value_have_the_properties_of_a_declaration_once_and_no_others(string part, string replacement)
    {
        Descriptor.ExpectRefused(ValueSchema.Json, part, replacement);
    }

    // The declaration of one position of the given type, with what that type needs.
    private static string Declaration(string type) => type switch
    {
        "Enum" => "{ 'Type': 'Enum', 'Enum': 'Color' }",
        "Owned" => "{ 'Type': 'Owned', 'OwnedType': 'Address' }",
        "Reference" => "{ 'Type': 'Reference', 'EntityType': 'Group' }",
        "List" => "{ 'Type': 'List', 'Element': { 'Type': 'Int32' } }",
        "Map" => "{ 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Int32' } }",
        "Set" => "{ 'Type': 'Set', 'Element': { 'Type': 'Int32' } }",
        _ => $"{{ 'Type': '{type}' }}",
    };

    // An entity type Holder with the given fields after its key, and what they may name: an
    // enum type, an owned type and an entity type with a Guid key. Written with ' for ".
    private static string Holder(string fields) => $$"""
        {
          "Enums": [ { "Id": 1, "Name": "Color", "Members": [ { "Name": "Red", "Value": 0 } ] } ],
          "OwnedTypes": [ { "Id": 1, "Name": "Address", "Fields": [ { "Id": 1, "Name": "City", "Type": "String" } ] } ],
          "EntityTypes": [
            { "Id": 1, "Name": "Holder", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, {{fields.Replace('\'', '"')}} ], "PrimaryKey": [1] },
            { "Id": 2, "Name": "Group", "Fields": [ { "Id": 1, "Name": "Key", "Type": "Guid" } ], "PrimaryKey": [1] }
          ]
        }
        """;

    private static void ExpectRefusedWithPlace(string json, params string[] place)
    {
        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(json));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.All(place, name => Assert.Contains(name, exception.Message));
    }
}
