namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Scalars;

// Boolean, Decimal and Guid read back exactly as written (brief of S05, Acceptance; Q03.1,
// Q03.4.1). A Decimal is the 16 bytes of a .NET decimal: it keeps its scale and its sign, so 1.0
// and 1.00, which are equal as numbers, read back as written, and so does a negative zero. A
// Decimal is therefore compared by decimal.GetBits. A Guid is its 16 bytes, never text, compared
// as a Guid, which compares all of them.
public sealed class ExactValueRoundTripTests
{
    [Fact]
    public void A_Boolean_reads_back_as_written()
    {
        using var item = TypedItem.OfType("Boolean");

        foreach (var value in new[] { false, true })
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetBoolean(item.Field("Value"), value);
            writer.SetBoolean(item.Field("Maybe"), !value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value, reader.GetBoolean(item.Field("Value")));
            Assert.Equal(!value, reader.GetBoolean(item.Field("Maybe")));
        }
    }

    [Fact]
    public void A_Decimal_reads_back_with_its_scale_and_its_sign()
    {
        decimal[] values =
        [
            0m, 1m, 1.0m, 1.00m, -1.000m, 0.5m, -0.5m, 123_456_789.123_456_789m,
            0.0000000000000000000000000001m, -0.0000000000000000000000000001m,
            decimal.MinValue, decimal.MaxValue,
            new decimal(0, 0, 0, isNegative: true, scale: 3),
            new decimal(lo: -1, mid: -1, hi: -1, isNegative: false, scale: 28),
        ];
        using var item = TypedItem.OfType("Decimal");

        foreach (var value in values)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetDecimal(item.Field("Value"), value);
            writer.SetDecimal(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(decimal.GetBits(value), decimal.GetBits(reader.GetDecimal(item.Field("Value"))));
            Assert.Equal(decimal.GetBits(value), decimal.GetBits(reader.GetDecimal(item.Field("Maybe"))));
        }
    }

    [Fact]
    public void A_Guid_reads_back_as_written()
    {
        Guid[] values =
        [
            Guid.Empty,
            new Guid([0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]),
            new Guid([255, 254, 253, 252, 251, 250, 249, 248, 247, 246, 245, 244, 243, 242, 241, 240]),
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            Guid.Parse("8c2f6a52-1d3b-4e7f-9a10-b2c4d6e8f012"),
        ];
        using var item = TypedItem.OfType("Guid");

        foreach (var value in values)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetGuid(item.Field("Value"), value);
            writer.SetGuid(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value, reader.GetGuid(item.Field("Value")));
            Assert.Equal(value, reader.GetGuid(item.Field("Maybe")));
        }
    }
}
