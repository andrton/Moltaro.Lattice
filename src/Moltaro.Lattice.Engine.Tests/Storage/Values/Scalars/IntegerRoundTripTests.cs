namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Scalars;

// Every integer type keeps every value of its width and reads it back as it was written (brief of
// S05, Acceptance; Q03.1): SetInteger and GetInteger for Int8 to Int64, SetUnsigned and GetUnsigned
// for UInt8 to UInt64. The one-byte types are tried with every value, the wider ones with their
// minimum, their maximum, the values next to them, the values around zero and a value with a
// different byte in every position. The nullable field Maybe keeps the same values; null is in
// Fields/NullAndMissingTests, and that a small integer is not widened in Layouts/FixedSizeTests.
public sealed class IntegerRoundTripTests
{
    [Theory]
    [InlineData("Int8", (long)sbyte.MinValue, (long)sbyte.MaxValue)]
    [InlineData("Int16", (long)short.MinValue, (long)short.MaxValue)]
    [InlineData("Int32", (long)int.MinValue, (long)int.MaxValue)]
    [InlineData("Int64", long.MinValue, long.MaxValue)]
    public void A_signed_integer_reads_back_as_written_from_its_minimum_to_its_maximum(string type, long minimum, long maximum)
    {
        using var item = TypedItem.OfType(type);

        foreach (var value in Samples(minimum, maximum))
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), value);
            writer.SetInteger(item.Field("Value"), value);
            writer.SetInteger(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value, reader.GetInteger(item.Field("Id")));
            Assert.Equal(value, reader.GetInteger(item.Field("Value")));
            Assert.Equal(value, reader.GetInteger(item.Field("Maybe")));
        }
    }

    [Theory]
    [InlineData("UInt8", (ulong)byte.MaxValue)]
    [InlineData("UInt16", (ulong)ushort.MaxValue)]
    [InlineData("UInt32", (ulong)uint.MaxValue)]
    [InlineData("UInt64", ulong.MaxValue)]
    public void An_unsigned_integer_reads_back_as_written_from_zero_to_its_maximum(string type, ulong maximum)
    {
        using var item = TypedItem.OfType(type);

        foreach (var value in Samples(maximum))
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetUnsigned(item.Field("Value"), value);
            writer.SetUnsigned(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(value, reader.GetUnsigned(item.Field("Value")));
            Assert.Equal(value, reader.GetUnsigned(item.Field("Maybe")));
        }
    }

    [Fact]
    public void Neighbouring_small_integers_keep_their_own_values()
    {
        // Packed without padding, each field next to the other: a value that spilled into its
        // neighbour, or a sign that was extended into it, would change the neighbour.
        using var item = RootArena.Of("""
            { 'Id': 2, 'Name': 'A', 'Type': 'Int8' },
            { 'Id': 3, 'Name': 'B', 'Type': 'UInt8' },
            { 'Id': 4, 'Name': 'C', 'Type': 'Int16' },
            { 'Id': 5, 'Name': 'D', 'Type': 'UInt16' },
            { 'Id': 6, 'Name': 'E', 'Type': 'Int8' },
            { 'Id': 7, 'Name': 'F', 'Type': 'Int32' },
            { 'Id': 8, 'Name': 'G', 'Type': 'UInt8' }
            """);
        (long A, ulong B, long C, ulong D, long E, long F, ulong G)[] rows =
        [
            (-1, 0, -1, 0, -1, -1, 0),
            (0, byte.MaxValue, 0, ushort.MaxValue, 0, 0, byte.MaxValue),
            (sbyte.MinValue, byte.MaxValue, short.MinValue, ushort.MaxValue, sbyte.MaxValue, int.MinValue, 1),
            (sbyte.MaxValue, 1, short.MaxValue, 1, sbyte.MinValue, int.MaxValue, byte.MaxValue),
        ];

        foreach (var row in rows)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetInteger(item.Field("A"), row.A);
            writer.SetUnsigned(item.Field("B"), row.B);
            writer.SetInteger(item.Field("C"), row.C);
            writer.SetUnsigned(item.Field("D"), row.D);
            writer.SetInteger(item.Field("E"), row.E);
            writer.SetInteger(item.Field("F"), row.F);
            writer.SetUnsigned(item.Field("G"), row.G);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(row, (
                reader.GetInteger(item.Field("A")),
                reader.GetUnsigned(item.Field("B")),
                reader.GetInteger(item.Field("C")),
                reader.GetUnsigned(item.Field("D")),
                reader.GetInteger(item.Field("E")),
                reader.GetInteger(item.Field("F")),
                reader.GetUnsigned(item.Field("G"))));
        }
    }

    // Every value of a one-byte type; for a wider type the bounds, their neighbours, the values
    // around zero, and for every byte position a value whose lowest set bit is there.
    private static IEnumerable<long> Samples(long minimum, long maximum)
    {
        if (maximum <= sbyte.MaxValue)
        {
            for (var value = minimum; value <= maximum; value++)
            {
                yield return value;
            }

            yield break;
        }

        long[] bounds = [minimum, minimum + 1, -1, 0, 1, maximum - 1, maximum, 0x0102_0304_0506_0708 & maximum, -(0x0102_0304_0506_0708 & maximum)];

        foreach (var value in bounds)
        {
            yield return value;
        }

        for (var shift = 8; shift < 63; shift += 8)
        {
            var power = 1L << shift;

            if (power < maximum)
            {
                yield return power;
                yield return -power;
                yield return power - 1;
            }
        }
    }

    private static IEnumerable<ulong> Samples(ulong maximum)
    {
        if (maximum <= byte.MaxValue)
        {
            for (var value = 0UL; value <= maximum; value++)
            {
                yield return value;
            }

            yield break;
        }

        ulong[] bounds = [0, 1, 2, maximum / 2, maximum / 2 + 1, maximum - 1, maximum, 0x8102_0304_0506_0708 & maximum];

        foreach (var value in bounds)
        {
            yield return value;
        }

        for (var shift = 8; shift < 64; shift += 8)
        {
            var power = 1UL << shift;

            if (power < maximum)
            {
                yield return power;
                yield return power - 1;
            }
        }
    }
}
