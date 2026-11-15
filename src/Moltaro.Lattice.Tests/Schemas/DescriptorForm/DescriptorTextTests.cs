namespace Moltaro.Lattice.Tests.Schemas.DescriptorForm;

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

    [Fact]
    public void Input_repeated_in_a_message_is_cut_to_a_readable_length()
    {
        var digits = new string('9', 100_000);
        var name = new string('n', 100_000);

        // A member value beyond every integer type, a property the form does not have, and a
        // field name given twice: refused by the reader and by the validation of the model.
        string[] variants =
        [
            Descriptor.With(Json, "'Value': 0", $"'Value': {digits}"),
            Descriptor.With(Json, "'Enums': [", $"'{name}': 1, 'Enums': ["),
            Descriptor.With(Json, "{ 'Id': 1, 'Name': 'Id', 'Type': 'Int64' }", $"{{ 'Id': 1, 'Name': 'Id', 'Type': 'Int64' }}, {{ 'Id': 2, 'Name': '{name}', 'Type': 'Int64' }}, {{ 'Id': 3, 'Name': '{name}', 'Type': 'Int64' }}"),
        ];

        foreach (var variant in variants)
        {
            var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(variant));

            Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
            Assert.InRange(exception.Message.Length, 1, 400);
            Assert.Contains("(100000 characters)", exception.Message);
        }
    }
}
