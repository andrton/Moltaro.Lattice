using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Fixture;

// The schemas of the reference fixture (brief of S05, "The fixture"; milestone contract,
// "Reproducible first fixture"). The entity types, their field types and their indexes are those of
// the table: User, Group, Role and Setting. No field is nullable, and an index is unique only where
// the table says so: Login of User and Name of Role. Strings are Plain, UTF-8 and Ordinal, except
// the fields the table makes Dictionary. The scalar form has the same entity types and keeps every
// index, without the collection fields and with every String Plain; its Unique marks are there only
// when asked. What the table leaves open is not stated here: the names of the indexes, of the
// collection fields and of the key of Setting, which are found by their type instead.
public sealed class SchemaFormTests
{
    public static TheoryData<string> Forms => FixtureForms.All;

    public static TheoryData<string> ScalarForms => new(FixtureForms.Scalar, FixtureForms.ScalarUnique);

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void Every_form_compiles_with_the_four_entity_types_of_the_table(string form)
    {
        var schema = FixtureForms.Compile(form);

        Assert.Equal(["Group", "Role", "Setting", "User"], schema.EntityTypes.Select(entityType => entityType.Name).Order(StringComparer.Ordinal));
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void Every_form_stores_its_text_in_UTF8_and_compares_it_Ordinal(string form)
    {
        var schema = FixtureForms.Compile(form);

        Assert.Equal(LatticeStringEncodingEnum.Utf8, schema.StringEncoding);
        Assert.All(Strings(schema), declaration => Assert.Equal(LatticeStringComparisonEnum.Ordinal, declaration.Comparison));
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void No_field_of_any_form_is_nullable(string form)
    {
        var schema = FixtureForms.Compile(form);

        Assert.All(schema.EntityTypes.SelectMany(entityType => entityType.Fields), field => Assert.False(field.Declaration.IsNullable, field.Name));
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void The_scalar_fields_have_the_types_of_the_table(string form)
    {
        var schema = FixtureForms.Compile(form);
        var user = FixtureForms.EntityType(schema, "User");
        var group = FixtureForms.EntityType(schema, "Group");
        var role = FixtureForms.EntityType(schema, "Role");
        var setting = FixtureForms.EntityType(schema, "Setting");

        AssertKey(user, "Id", LatticeFieldTypeEnum.Int64);
        AssertField(user, "Login", LatticeFieldTypeEnum.String);
        AssertField(user, "Email", LatticeFieldTypeEnum.String);
        AssertField(user, "DisplayName", LatticeFieldTypeEnum.String);
        AssertField(user, "Region", LatticeFieldTypeEnum.String);
        AssertField(user, "IsActive", LatticeFieldTypeEnum.Boolean);
        AssertKey(group, "Id", LatticeFieldTypeEnum.Int64);
        AssertField(group, "Name", LatticeFieldTypeEnum.String);
        AssertKey(role, "Id", LatticeFieldTypeEnum.Int64);
        AssertField(role, "Name", LatticeFieldTypeEnum.String);
        Assert.Equal(LatticeFieldTypeEnum.String, setting.Key.Declaration.Type);
        AssertField(setting, "Section", LatticeFieldTypeEnum.String);
        AssertField(setting, "Name", LatticeFieldTypeEnum.String);
        AssertField(setting, "Value", LatticeFieldTypeEnum.String);
    }

    [Fact(Skip = NotImplemented.ReferenceFixture)]
    public void The_full_form_has_the_collections_and_the_Dictionary_strings_of_the_table()
    {
        var schema = FixtureForms.Compile(FixtureForms.Full);
        var user = FixtureForms.EntityType(schema, "User");
        var group = FixtureForms.EntityType(schema, "Group");
        var role = FixtureForms.EntityType(schema, "Role");
        var setting = FixtureForms.EntityType(schema, "Setting");

        // Beyond the scalar fields, one collection field for each entity type.
        Assert.Equal(7, user.Fields.Count);
        Assert.Equal(3, group.Fields.Count);
        Assert.Equal(3, role.Fields.Count);
        Assert.Equal(5, setting.Fields.Count);

        // An ordered List of Group references, an ordered List of Role references, a Set of
        // Dictionary permission strings, a Map of String keys to String values.
        var groups = Assert.Single(user.Fields, field => field.Declaration.Type == LatticeFieldTypeEnum.List).Declaration.Element!;
        Assert.Equal(LatticeFieldTypeEnum.Reference, groups.Type);
        Assert.Equal("Group", groups.ReferencedEntityTypeName);

        var roles = Assert.Single(group.Fields, field => field.Declaration.Type == LatticeFieldTypeEnum.List).Declaration.Element!;
        Assert.Equal(LatticeFieldTypeEnum.Reference, roles.Type);
        Assert.Equal("Role", roles.ReferencedEntityTypeName);

        var permissions = Assert.Single(role.Fields, field => field.Declaration.Type == LatticeFieldTypeEnum.Set).Declaration.Element!;
        Assert.Equal(LatticeFieldTypeEnum.String, permissions.Type);
        Assert.Equal(LatticeStringStorageEnum.Dictionary, permissions.StringStorage);

        var options = Assert.Single(setting.Fields, field => field.Declaration.Type == LatticeFieldTypeEnum.Map).Declaration;
        Assert.Equal(LatticeFieldTypeEnum.String, options.Key!.Type);
        Assert.Equal(LatticeFieldTypeEnum.String, options.Value!.Type);

        // Region and Section are Dictionary strings, and so are the permissions; every other text is
        // Plain.
        Assert.Equal(LatticeStringStorageEnum.Dictionary, FixtureForms.Field(user, "Region").Declaration.StringStorage);
        Assert.Equal(LatticeStringStorageEnum.Dictionary, FixtureForms.Field(setting, "Section").Declaration.StringStorage);
        Assert.Equal(3, Strings(schema).Count(declaration => declaration.StringStorage == LatticeStringStorageEnum.Dictionary));
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(ScalarForms))]
    public void The_scalar_form_has_no_collection_field_and_every_String_is_Plain(string form)
    {
        var schema = FixtureForms.Compile(form);

        Assert.Equal(6, FixtureForms.EntityType(schema, "User").Fields.Count);
        Assert.Equal(2, FixtureForms.EntityType(schema, "Group").Fields.Count);
        Assert.Equal(2, FixtureForms.EntityType(schema, "Role").Fields.Count);
        Assert.Equal(4, FixtureForms.EntityType(schema, "Setting").Fields.Count);
        Assert.All(
            schema.EntityTypes.SelectMany(entityType => entityType.Fields),
            field => Assert.DoesNotContain(field.Declaration.Type, new[] { LatticeFieldTypeEnum.List, LatticeFieldTypeEnum.Map, LatticeFieldTypeEnum.Set, LatticeFieldTypeEnum.Reference, LatticeFieldTypeEnum.Owned }));
        Assert.All(Strings(schema), declaration => Assert.Equal(LatticeStringStorageEnum.Plain, declaration.StringStorage));
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [MemberData(nameof(Forms))]
    public void Every_form_has_the_indexes_of_the_table(string form)
    {
        var schema = FixtureForms.Compile(form);

        Assert.Equal(["Email", "Login", "Region+Login"], IndexesOf(schema, "User"));
        Assert.Equal(["Name"], IndexesOf(schema, "Group"));
        Assert.Equal(["Name"], IndexesOf(schema, "Role"));
        Assert.Equal(["Section+Name"], IndexesOf(schema, "Setting"));
    }

    [Theory(Skip = NotImplemented.ReferenceFixture)]
    [InlineData(FixtureForms.Full, true)]
    [InlineData(FixtureForms.ScalarUnique, true)]
    [InlineData(FixtureForms.Scalar, false)]
    public void An_index_is_unique_only_where_the_table_says_so_and_in_the_scalar_form_only_when_asked(string form, bool unique)
    {
        var schema = FixtureForms.Compile(form);
        string[] expected = unique ? ["Role Name", "User Login"] : [];

        var uniqueIndexes = schema.EntityTypes
            .SelectMany(entityType => entityType.Indexes.Where(index => index.IsUnique).Select(index => $"{entityType.Name} {PartsOf(index)}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, uniqueIndexes);
    }

    // The declarations of text in the schema: every String field, element, key and value.
    private static List<LatticeValueDeclaration> Strings(LatticeSchema schema)
    {
        var strings = new List<LatticeValueDeclaration>();

        foreach (var field in schema.EntityTypes.SelectMany(entityType => entityType.Fields))
        {
            LatticeValueDeclaration?[] positions = [field.Declaration, field.Declaration.Element, field.Declaration.Key, field.Declaration.Value];
            strings.AddRange(positions.OfType<LatticeValueDeclaration>().Where(declaration => declaration.Type == LatticeFieldTypeEnum.String));
        }

        return strings;
    }

    // The indexes of the entity type, each by the names of its fields in order.
    private static IEnumerable<string> IndexesOf(LatticeSchema schema, string entityTypeName) =>
        FixtureForms.EntityType(schema, entityTypeName).Indexes.Select(PartsOf).Order(StringComparer.Ordinal);

    private static string PartsOf(LatticeIndex index) => string.Join("+", index.Parts.Select(part => part.Field.Name));

    private static void AssertKey(LatticeEntityType entityType, string name, LatticeFieldTypeEnum type)
    {
        Assert.Equal(name, entityType.Key.Name);
        Assert.Equal(type, entityType.Key.Declaration.Type);
    }

    private static void AssertField(LatticeEntityType entityType, string name, LatticeFieldTypeEnum type) =>
        Assert.Equal(type, FixtureForms.Field(entityType, name).Declaration.Type);
}
