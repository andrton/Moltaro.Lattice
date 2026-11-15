namespace Moltaro.Lattice.Tests.Schemas.EnumTypes;

// Flags: an enum type that holds a set of flags in one integer. Each flag is a member with
// exactly one bit; a member with several bits names a combination of declared flags. A value
// is zero, the empty set, or any combination of declared bits. A bit is a bit of the width of
// the underlying type, the sign bit of a signed type included, and a value outside the range
// of that type is never valid.
public sealed class FlagsEnumTests
{
    private const string Access = "{ 'Name': 'Read', 'Value': 1 }, { 'Name': 'Write', 'Value': 2 }, { 'Name': 'Export', 'Value': 4 }, { 'Name': 'ReadWrite', 'Value': 3 }";

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    // Read and Export, a combination that no member names.
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    // A bit that no flag declares, alone or with declared ones. It is not removed to make
    // the value valid.
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(16, false)]
    [InlineData(255, false)]
    public void Flags_value_is_zero_or_any_combination_of_declared_bits(int value, bool isValid)
    {
        var access = EnumSchema.Flags("UInt8", Access);

        Assert.True(access.IsFlags);
        Assert.Equal(isValid, access.IsValid(value));
    }

    [Fact]
    public void Flags_type_may_name_the_empty_set_a_combination_and_a_flag_twice()
    {
        var access = EnumSchema.Flags(
            "UInt8",
            "{ 'Name': 'None', 'Value': 0 }, { 'Name': 'Read', 'Value': 1, 'Canonical': true }, { 'Name': 'View', 'Value': 1 }, { 'Name': 'Write', 'Value': 2 }, { 'Name': 'ReadWrite', 'Value': 3 }");

        Assert.Equal(
            [("None", true), ("Read", true), ("View", false), ("Write", true), ("ReadWrite", true)],
            access.Members.Select(member => (member.Name, member.IsCanonical)));

        // Neither a combination nor a second name adds a bit.
        Assert.True(access.IsValid(3));
        Assert.False(access.IsValid(4));
    }

    [Theory]
    // A combination with a bit that no flag declares.
    [InlineData("UInt8", "{ 'Name': 'Read', 'Value': 1 }, { 'Name': 'ReadAndExport', 'Value': 5 }")]
    // Several bits and no flag of its own.
    [InlineData("UInt8", "{ 'Name': 'Both', 'Value': 3 }")]
    // Every bit of the width, when only two are declared.
    [InlineData("Int32", "{ 'Name': 'Read', 'Value': 1 }, { 'Name': 'Write', 'Value': 2 }, { 'Name': 'All', 'Value': -1 }")]
    [InlineData("UInt8", "{ 'Name': 'Read', 'Value': 1 }, { 'Name': 'All', 'Value': 255 }")]
    public void Flags_member_with_a_bit_that_no_flag_declares_is_refused(string underlyingType, string members)
    {
        // A Flags type of the same width with one flag compiles.
        EnumSchema.Flags(underlyingType, "{ 'Name': 'Read', 'Value': 1 }");

        var json = EnumSchema.Json(underlyingType, isFlags: true, members);

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Compile(json));
    }

    [Fact]
    public void Combination_of_every_bit_of_a_signed_type_may_be_named_when_every_bit_is_declared()
    {
        var bits = EnumSchema.Flags(
            "Int8",
            "{ 'Name': 'B0', 'Value': 1 }, { 'Name': 'B1', 'Value': 2 }, { 'Name': 'B2', 'Value': 4 }, { 'Name': 'B3', 'Value': 8 }, " +
            "{ 'Name': 'B4', 'Value': 16 }, { 'Name': 'B5', 'Value': 32 }, { 'Name': 'B6', 'Value': 64 }, { 'Name': 'B7', 'Value': -128 }, " +
            "{ 'Name': 'All', 'Value': -1 }");

        Assert.True(bits.IsValid(-1));
        Assert.True(bits.IsValid(-128));
        Assert.True(bits.IsValid(127));

        // The same bits as -1 and -128, but outside the range of Int8.
        Assert.False(bits.IsValid(255));
        Assert.False(bits.IsValid(128));
    }

    [Theory]
    // The sign bit, and the number with the same bits in the width that the type cannot hold.
    [InlineData("Int8", "-128", "128")]
    [InlineData("Int16", "-32768", "32768")]
    [InlineData("Int32", "-2147483648", "2147483648")]
    [InlineData("Int64", "-9223372036854775808", "9223372036854775808")]
    public void Sign_bit_of_a_signed_flags_type_is_one_bit_like_the_others(string underlyingType, string signBit, string sameBitsOutside)
    {
        var flags = EnumSchema.Flags(underlyingType, $"{{ 'Name': 'Low', 'Value': 1 }}, {{ 'Name': 'Sign', 'Value': {signBit} }}");
        var sign = EnumSchema.Value(signBit);
        var outside = EnumSchema.Value(sameBitsOutside);

        // The sign bit alone and with the low bit. Widening a negative value to a wider
        // integer does not turn its sign into bits that no flag declares.
        Assert.True(flags.IsValid(sign));
        Assert.True(flags.IsValid(sign + 1));
        Assert.True(flags.IsValid(0));

        // Every bit, or one that no flag declares.
        Assert.False(flags.IsValid(-1));
        Assert.False(flags.IsValid(2));

        // A number outside the range is not cut to the width to make it valid.
        Assert.False(flags.IsValid(outside));
        Assert.False(flags.IsValid(outside + 1));
    }

    [Theory]
    // The high bit, the negative number with the same bits in the width, and the first number
    // beyond the width.
    [InlineData("UInt8", "128", "-128", "256")]
    [InlineData("UInt16", "32768", "-32768", "65536")]
    [InlineData("UInt32", "2147483648", "-2147483648", "4294967296")]
    [InlineData("UInt64", "9223372036854775808", "-9223372036854775808", "18446744073709551616")]
    public void High_bit_of_an_unsigned_flags_type_is_one_bit_and_no_negative_value_is_valid(string underlyingType, string highBit, string sameBitsNegative, string beyond)
    {
        var flags = EnumSchema.Flags(underlyingType, $"{{ 'Name': 'Low', 'Value': 1 }}, {{ 'Name': 'High', 'Value': {highBit} }}");
        var high = EnumSchema.Value(highBit);
        var negative = EnumSchema.Value(sameBitsNegative);

        Assert.True(flags.IsValid(high));
        Assert.True(flags.IsValid(high + 1));

        Assert.False(flags.IsValid(negative));
        Assert.False(flags.IsValid(negative + 1));
        Assert.False(flags.IsValid(-1));

        // Beyond the width, also where the bits inside it are declared ones.
        Assert.False(flags.IsValid(EnumSchema.Value(beyond)));
        Assert.False(flags.IsValid(EnumSchema.Value(beyond) + high));
    }

    [Fact]
    public void Enum_type_is_flags_only_when_the_descriptor_says_so()
    {
        // Values that are powers of two do not make an enum type a set of flags.
        var access = EnumSchema.Ordinary("UInt8", "{ 'Name': 'Read', 'Value': 1 }, { 'Name': 'Write', 'Value': 2 }, { 'Name': 'Export', 'Value': 4 }");

        Assert.False(access.IsFlags);
        Assert.True(access.IsValid(4));
        Assert.False(access.IsValid(3));
        Assert.False(access.IsValid(0));
    }
}
