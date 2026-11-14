namespace Moltaro.Lattice.Schema;

// The values of one of the eight integer types, for the values of an enum type, which are
// held as Int128 whatever its underlying type is.
internal readonly record struct IntegerRange(Int128 Minimum, Int128 Maximum, UInt128 WidthMask)
{
    public static IntegerRange Of(LatticeFieldTypeEnum type) => type switch
    {
        LatticeFieldTypeEnum.Int8 => new(sbyte.MinValue, sbyte.MaxValue, byte.MaxValue),
        LatticeFieldTypeEnum.UInt8 => new(byte.MinValue, byte.MaxValue, byte.MaxValue),
        LatticeFieldTypeEnum.Int16 => new(short.MinValue, short.MaxValue, ushort.MaxValue),
        LatticeFieldTypeEnum.UInt16 => new(ushort.MinValue, ushort.MaxValue, ushort.MaxValue),
        LatticeFieldTypeEnum.Int32 => new(int.MinValue, int.MaxValue, uint.MaxValue),
        LatticeFieldTypeEnum.UInt32 => new(uint.MinValue, uint.MaxValue, uint.MaxValue),
        LatticeFieldTypeEnum.Int64 => new(long.MinValue, long.MaxValue, ulong.MaxValue),
        LatticeFieldTypeEnum.UInt64 => new(ulong.MinValue, ulong.MaxValue, ulong.MaxValue),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public bool Contains(Int128 value) => value >= Minimum && value <= Maximum;

    // The bits of a value within the width of the type, as the type stores it in two's
    // complement: the sign bit of a signed type is one bit like the others, and a negative
    // value widened to Int128 brings no bits from above the width. Only meaningful for a
    // value the range contains.
    public UInt128 BitsOf(Int128 value) => unchecked((UInt128)value) & WidthMask;
}
