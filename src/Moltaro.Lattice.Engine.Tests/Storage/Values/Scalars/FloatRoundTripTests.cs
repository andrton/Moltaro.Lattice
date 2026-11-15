namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Scalars;

// Float32 and Float64 keep every value they are given, the special values included: NaN, both
// infinities, negative zero, the smallest subnormal and the extremes (brief of S05, Acceptance;
// Q03.4.1, binary32 and binary64). A value is compared by its bits, because NaN is not equal to
// itself as a number and negative zero equals positive zero.
public sealed class FloatRoundTripTests
{
    private static readonly float[] Singles =
    [
        0f, -0f, 1f, -1f, 3.1415927f, float.Epsilon, -float.Epsilon, 1.17549435E-38f,
        float.MinValue, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity,
    ];

    private static readonly double[] Doubles =
    [
        0d, -0d, 1d, -1d, Math.PI, double.Epsilon, -double.Epsilon, 2.2250738585072014E-308,
        double.MinValue, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
    ];

    [Fact]
    public void A_Float32_reads_back_with_the_same_bits()
    {
        using var item = TypedItem.OfType("Float32");

        foreach (var value in Singles)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetFloat32(item.Field("Value"), value);
            writer.SetFloat32(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(reader.GetFloat32(item.Field("Value"))));
            Assert.Equal(BitConverter.SingleToInt32Bits(value), BitConverter.SingleToInt32Bits(reader.GetFloat32(item.Field("Maybe"))));
        }
    }

    [Fact]
    public void A_Float64_reads_back_with_the_same_bits()
    {
        using var item = TypedItem.OfType("Float64");

        foreach (var value in Doubles)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetFloat64(item.Field("Value"), value);
            writer.SetFloat64(item.Field("Maybe"), value);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(reader.GetFloat64(item.Field("Value"))));
            Assert.Equal(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(reader.GetFloat64(item.Field("Maybe"))));
        }
    }
}
