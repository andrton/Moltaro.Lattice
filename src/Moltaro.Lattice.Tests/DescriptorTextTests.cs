namespace Moltaro.Lattice.Tests;

// What the reader refuses in the text of a descriptor itself: text that is not well-formed,
// and a number too large for every integer type. Each is InvalidSchema with a message that
// says what is wrong, never another exception.
public sealed class DescriptorTextTests
{
    private const string Json = """
        {
          "Enums": [ { "Id": 1, "Name": "Level", "UnderlyingType": "UInt64", "Members": [ { "Name": "Low", "Value": 0 } ] } ],
          "EntityTypes": [
            { "Id": 1, "Name": "Note", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] }
          ]
        }
        """;

    [Theory]
    // An escaped surrogate without its pair, in a value and in a property name.
    [InlineData("'Name': 'Note'", "'Name': 'No\\ud800te'")]
    [InlineData("'Enums': [", "'\\udc00': 1, 'Enums': [")]
    public void Escaped_text_that_is_not_well_formed_is_refused(string part, string replacement)
    {
        var exception = Descriptor.ExpectRefused(Json, part, replacement);

        Assert.Contains("well-formed", exception.Message);
    }

    [Fact]
    public void Text_with_a_surrogate_without_its_pair_is_refused()
    {
        // Built here rather than written in an attribute, which cannot hold such a string.
        var loneSurrogate = new string((char)0xD800, 1);

        var exception = Descriptor.ExpectRefused(Json, "'Name': 'Note'", $"'Name': 'No{loneSurrogate}te'");

        Assert.Contains("well-formed", exception.Message);
    }

    [Theory]
    // One beyond Int128.MaxValue and one below Int128.MinValue.
    [InlineData("170141183460469231731687303715884105728")]
    [InlineData("-170141183460469231731687303715884105729")]
    public void Member_value_beyond_every_integer_type_is_refused_as_out_of_range(string value)
    {
        var exception = Descriptor.ExpectRefused(Json, "'Value': 0", $"'Value': {value}");

        Assert.Contains("outside the range", exception.Message);
        Assert.Contains("Level.Low", exception.Message);
    }
}
