using System.Globalization;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Fields;

// A value outside the range of the width of its field is ValidationFailed at the call; it is never
// cut, wrapped or widened to make it fit (brief of S05, "Which member serves which field"; Q03.1).
// The width of an Enum field is that of its underlying type. A refused call did not set the field,
// and a valid value may follow (brief, "The life of a writer"); the bounds themselves are stored,
// as Scalars/IntegerRoundTripTests and EnumRoundTripTests show.
public sealed class RangeTests
{
    // Enum types of four widths, each with the member 1 that the valid value uses.
    private const string Enums = """
        { 'Id': 1, 'Name': 'Small', 'UnderlyingType': 'Int8', 'Members': [ { 'Name': 'One', 'Value': 1 } ] },
        { 'Id': 2, 'Name': 'Medium', 'UnderlyingType': 'Int32', 'Members': [ { 'Name': 'One', 'Value': 1 } ] },
        { 'Id': 3, 'Name': 'Byte', 'UnderlyingType': 'UInt8', 'Members': [ { 'Name': 'One', 'Value': 1 } ] },
        { 'Id': 4, 'Name': 'Word', 'UnderlyingType': 'UInt16', 'Members': [ { 'Name': 'One', 'Value': 1 } ] }
        """;

    [Theory]
    [InlineData("'Type': 'Int8'", "-129", "128", "-9223372036854775808", "9223372036854775807", "256", "-256")]
    [InlineData("'Type': 'Int16'", "-32769", "32768", "-9223372036854775808", "9223372036854775807", "65536")]
    [InlineData("'Type': 'Int32'", "-2147483649", "2147483648", "-9223372036854775808", "9223372036854775807", "4294967296")]
    [InlineData("'Type': 'Enum', 'Enum': 'Small'", "-129", "128", "-9223372036854775808", "9223372036854775807")]
    [InlineData("'Type': 'Enum', 'Enum': 'Medium'", "-2147483649", "2147483648")]
    public void A_signed_value_outside_the_range_of_its_width_is_ValidationFailed(string declaration, params string[] values)
    {
        using var item = TypedItem.Open(declaration, Enums);

        foreach (var text in values)
        {
            var value = long.Parse(text, CultureInfo.InvariantCulture);
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);

            WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetInteger(item.Field("Value"), value));
            WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetInteger(item.Field("Maybe"), value));

            // Neither field was set: a valid value follows, and the other stays null.
            writer.SetInteger(item.Field("Value"), 1);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(1, reader.GetInteger(item.Field("Value")));
            Assert.True(reader.IsNull(item.Field("Maybe")));
        }
    }

    [Theory]
    [InlineData("'Type': 'UInt8'", "256", "65535", "18446744073709551615")]
    [InlineData("'Type': 'UInt16'", "65536", "18446744073709551615")]
    [InlineData("'Type': 'UInt32'", "4294967296", "18446744073709551615")]
    [InlineData("'Type': 'Enum', 'Enum': 'Byte'", "256", "18446744073709551615")]
    [InlineData("'Type': 'Enum', 'Enum': 'Word'", "65536", "4294967296")]
    public void An_unsigned_value_outside_the_range_of_its_width_is_ValidationFailed(string declaration, params string[] values)
    {
        using var item = TypedItem.Open(declaration, Enums);

        foreach (var text in values)
        {
            var value = ulong.Parse(text, CultureInfo.InvariantCulture);
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);

            WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetUnsigned(item.Field("Value"), value));
            WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetUnsigned(item.Field("Maybe"), value));

            writer.SetUnsigned(item.Field("Value"), 1);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(1UL, reader.GetUnsigned(item.Field("Value")));
            Assert.True(reader.IsNull(item.Field("Maybe")));
        }
    }
}
