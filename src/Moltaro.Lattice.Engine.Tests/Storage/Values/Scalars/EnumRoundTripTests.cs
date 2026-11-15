using System.Globalization;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Scalars;

// An Enum field is stored at the width of the underlying type of its enum type and reads back its
// value as written (brief of S05, Contract and "Which member serves which field"; Q03.5, Q03.6):
// through SetInteger and GetInteger when the underlying type is signed, through SetUnsigned and
// GetUnsigned when it is unsigned, Flags included. Whether a value is a declared member is checked
// from S07 on; these tests use declared members only, at the bounds of every width and, for Flags,
// the highest bit and a declared combination. The width itself is in Layouts/FixedSizeTests.
public sealed class EnumRoundTripTests
{
    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("Int8", false, "-128", "-1", "0", "127")]
    [InlineData("Int16", false, "-32768", "-1", "0", "32767")]
    [InlineData("Int32", false, "-2147483648", "-1", "0", "2147483647")]
    [InlineData("Int64", false, "-9223372036854775808", "-1", "0", "9223372036854775807")]
    [InlineData("Int8", true, "1", "64", "-128", "-127")]
    [InlineData("Int32", true, "1", "1073741824", "-2147483648", "-1073741823")]
    [InlineData("Int64", true, "1", "2", "-9223372036854775808", "-9223372036854775805")]
    public void An_Enum_of_a_signed_underlying_type_reads_back_its_declared_values(string underlyingType, bool flags, params string[] values)
    {
        using var item = OpenWithMembers(underlyingType, flags, values);

        foreach (var text in values)
        {
            var value = long.Parse(text, CultureInfo.InvariantCulture);
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetInteger(item.Field("Value"), value);
            writer.SetInteger(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value, reader.GetInteger(item.Field("Value")));
            Assert.Equal(value, reader.GetInteger(item.Field("Maybe")));
        }
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("UInt8", false, "0", "1", "255")]
    [InlineData("UInt16", false, "0", "1", "65535")]
    [InlineData("UInt32", false, "0", "2147483648", "4294967295")]
    [InlineData("UInt64", false, "0", "9223372036854775808", "18446744073709551615")]
    [InlineData("UInt8", true, "1", "128", "129")]
    [InlineData("UInt16", true, "1", "32768", "32769")]
    [InlineData("UInt64", true, "1", "9223372036854775808", "9223372036854775809")]
    public void An_Enum_of_an_unsigned_underlying_type_reads_back_its_declared_values(string underlyingType, bool flags, params string[] values)
    {
        using var item = OpenWithMembers(underlyingType, flags, values);

        foreach (var text in values)
        {
            var value = ulong.Parse(text, CultureInfo.InvariantCulture);
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetUnsigned(item.Field("Value"), value);
            writer.SetUnsigned(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value, reader.GetUnsigned(item.Field("Value")));
            Assert.Equal(value, reader.GetUnsigned(item.Field("Maybe")));
        }
    }

    // An Item whose fields Value and Maybe are of the enum type Code, which declares a member for
    // each of the values. For a Flags type the values are single bits and a combination of them.
    private static RootArena OpenWithMembers(string underlyingType, bool flags, string[] values)
    {
        var members = values.Select((value, index) => $"{{ 'Name': 'Member{index}', 'Value': {value} }}");
        var enums = $"{{ 'Id': 1, 'Name': 'Code', 'UnderlyingType': '{underlyingType}', 'Flags': {(flags ? "true" : "false")}, 'Members': [ {string.Join(", ", members)} ] }}";

        return TypedItem.Open("'Type': 'Enum', 'Enum': 'Code'", enums);
    }
}
