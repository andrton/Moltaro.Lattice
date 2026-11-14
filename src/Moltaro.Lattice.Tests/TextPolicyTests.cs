namespace Moltaro.Lattice.Tests;

// Text policies: a String field declares how it is compared and how its text is stored, and
// the database declares one encoding for all of its text. A policy that is not stated has its
// default: Ordinal, Plain, UTF-8; and a field is not nullable unless it says so.
public sealed class TextPolicyTests
{
    private const string Json = """
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Setting",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Scope", "Type": "String", "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" },
                { "Id": 3, "Name": "Name", "Type": "String", "Comparison": "Ordinal", "StringStorage": "Plain" },
                { "Id": 4, "Name": "Value", "Type": "String", "Nullable": true },
                { "Id": 5, "Name": "Priority", "Type": "UInt8" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByScope", "Fields": [2] } ]
            },
            {
              "Id": 2, "Name": "Tag",
              "Fields": [
                { "Id": 1, "Name": "Label", "Type": "String", "Comparison": "OrdinalIgnoreCase" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    [Fact(Skip = NotImplemented.SchemaDeclarations)]
    public void Policies_and_nullability_have_their_defaults_when_the_descriptor_states_none()
    {
        var schema = LatticeSchema.Compile(Json);
        Assert.True(schema.TryGetEntityType("Setting", out var setting));

        Assert.Equal(LatticeStringEncodingEnum.Utf8, schema.StringEncoding);
        Assert.Empty(schema.EnumTypes);

        Assert.True(setting.TryGetField("Value", out var value));
        Assert.Equal(LatticeStringComparisonEnum.Ordinal, value.Declaration.Comparison);
        Assert.Equal(LatticeStringStorageEnum.Plain, value.Declaration.StringStorage);
        Assert.True(value.Declaration.IsNullable);

        // A field that does not say Nullable is not nullable.
        Assert.True(setting.TryGetField("Priority", out var priority));
        Assert.False(priority.Declaration.IsNullable);
        Assert.False(setting.Key.Declaration.IsNullable);

        // A field that is not a String has no text policy, and only an Enum field has an enum type.
        Assert.Null(priority.Declaration.Comparison);
        Assert.Null(priority.Declaration.StringStorage);
        Assert.Null(priority.Declaration.EnumType);
        Assert.Null(value.Declaration.EnumType);
    }

    [Fact(Skip = NotImplemented.SchemaDeclarations)]
    public void String_field_declares_how_it_is_compared_and_how_its_text_is_stored()
    {
        var schema = LatticeSchema.Compile(Json);
        Assert.True(schema.TryGetEntityType("Setting", out var setting));

        Assert.True(setting.TryGetField("Scope", out var scope));
        Assert.Equal(LatticeStringComparisonEnum.OrdinalIgnoreCase, scope.Declaration.Comparison);
        Assert.Equal(LatticeStringStorageEnum.Dictionary, scope.Declaration.StringStorage);
        Assert.Equal(typeof(string), scope.Declaration.ValueType);

        // The defaults may also be stated.
        Assert.True(setting.TryGetField("Name", out var name));
        Assert.Equal(LatticeStringComparisonEnum.Ordinal, name.Declaration.Comparison);
        Assert.Equal(LatticeStringStorageEnum.Plain, name.Declaration.StringStorage);

        // A primary key is compared by its policy too.
        Assert.True(schema.TryGetEntityType("Tag", out var tag));
        Assert.Equal(LatticeStringComparisonEnum.OrdinalIgnoreCase, tag.Key.Declaration.Comparison);
    }

    [Theory(Skip = NotImplemented.SchemaDeclarations)]
    [InlineData("Utf8", LatticeStringEncodingEnum.Utf8)]
    [InlineData("Utf16", LatticeStringEncodingEnum.Utf16)]
    public void Schema_declares_one_string_encoding_for_the_database(string declared, LatticeStringEncodingEnum expected)
    {
        var json = Descriptor.With(Json, "'EntityTypes': [", $"'StringEncoding': '{declared}', 'EntityTypes': [");

        var schema = LatticeSchema.Compile(json);

        Assert.Equal(expected, schema.StringEncoding);
    }

    [Theory(Skip = NotImplemented.SchemaDeclarations)]
    // Language-aware comparisons are not part of the model.
    [InlineData("'Comparison': 'OrdinalIgnoreCase', 'StringStorage': 'Dictionary'", "'Comparison': 'InvariantCultureIgnoreCase', 'StringStorage': 'Dictionary'")]
    [InlineData("'Comparison': 'OrdinalIgnoreCase', 'StringStorage': 'Dictionary'", "'Comparison': 'CurrentCulture', 'StringStorage': 'Dictionary'")]
    // A storage the model does not have.
    [InlineData("'Comparison': 'OrdinalIgnoreCase', 'StringStorage': 'Dictionary'", "'Comparison': 'OrdinalIgnoreCase', 'StringStorage': 'Interned'")]
    // An encoding the model does not have.
    [InlineData("'EntityTypes': [", "'StringEncoding': 'Utf32', 'EntityTypes': [")]
    [InlineData("'EntityTypes': [", "'StringEncoding': 'Latin1', 'EntityTypes': [")]
    public void Unknown_text_policy_is_refused(string part, string replacement)
    {
        Descriptor.ExpectRefused(Json, part, replacement);
    }

    [Theory(Skip = NotImplemented.SchemaDeclarations)]
    [InlineData("{ 'Id': 5, 'Name': 'Priority', 'Type': 'UInt8' }", "{ 'Id': 5, 'Name': 'Priority', 'Type': 'UInt8', 'Comparison': 'OrdinalIgnoreCase' }")]
    [InlineData("{ 'Id': 5, 'Name': 'Priority', 'Type': 'UInt8' }", "{ 'Id': 5, 'Name': 'Priority', 'Type': 'UInt8', 'StringStorage': 'Dictionary' }")]
    public void Text_policy_of_a_field_that_is_not_a_string_is_refused(string part, string replacement)
    {
        // The policy would mean nothing, so it is a mistake in the descriptor, not something
        // to ignore.
        Descriptor.ExpectRefused(Json, part, replacement);
    }
}
