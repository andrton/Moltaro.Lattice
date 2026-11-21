using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Storage.Values;

// The bytes a root stores for a value of a fixed-width type, written and read in this one place: the
// root writer writes a field through it, the search key of an index writes the values of a key through
// it, and the root reader and the order of an index read them back through it (milestone contract,
// "Native layout and allocation"). An entry of an index takes the bytes of its fixed parts from its
// root as they lie and does not write them again (Ordered.IndexLayout.WriteEntry), so no value is
// written in two forms.
//
// A place is the span of the value at its width, which the caller takes from the layout: 1, 2, 4 or 8
// bytes for an integer, as its type has; 1 for a Boolean, 0 or 1; 4 for a Float32 and a DateOnly, its
// bits and its day number; 8 for a Float64, its bits, and for a DateTimeOffset, a TimeOnly and a
// TimeSpan, their UTC ticks, ticks since midnight and signed ticks; 16 for a Decimal, the four integers
// of decimal.GetBits, and for a Guid, its bytes in the order of Guid.TryWriteBytes. Every number is in
// the byte order of the machine, as everything in native memory is. Nothing here allocates, checks a
// range or reads the schema: the callers have checked their values, and an integer is cut to the width
// of its place, which the caller made wide enough for it.
internal static class StoredScalars
{
    // Writes an integer, signed or unsigned, at the width of the place.
    // Inlined, so that the switch on the width lies in RootWriter.SetInteger as it did before the stored
    // form moved here; out of line it is one more call for every integer and Enum field written (JIT
    // listing at full optimization).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteInteger(Span<byte> place, long value)
    {
        switch (place.Length)
        {
            case sizeof(sbyte):
                place[0] = (byte)(sbyte)value;
                break;
            case sizeof(short):
                MemoryMarshal.Write(place, (short)value);
                break;
            case sizeof(int):
                MemoryMarshal.Write(place, (int)value);
                break;
            default:
                MemoryMarshal.Write(place, value);
                break;
        }
    }

    // Inlined into RootWriter.SetUnsigned, as WriteInteger is (see there).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUnsigned(Span<byte> place, ulong value)
    {
        switch (place.Length)
        {
            case sizeof(byte):
                place[0] = (byte)value;
                break;
            case sizeof(ushort):
                MemoryMarshal.Write(place, (ushort)value);
                break;
            case sizeof(uint):
                MemoryMarshal.Write(place, (uint)value);
                break;
            default:
                MemoryMarshal.Write(place, value);
                break;
        }
    }

    public static void WriteBoolean(Span<byte> place, bool value) => place[0] = value ? (byte)1 : (byte)0;

    // The bits as they are, those of a NaN and the sign of a zero included.
    public static void WriteFloat32(Span<byte> place, float value) => MemoryMarshal.Write(place, BitConverter.SingleToInt32Bits(value));

    public static void WriteFloat64(Span<byte> place, double value) => MemoryMarshal.Write(place, BitConverter.DoubleToInt64Bits(value));

    // The four integers of decimal.GetBits, so that the scale and the sign are kept.
    public static void WriteDecimal(Span<byte> place, decimal value)
    {
        Span<int> bits = stackalloc int[4];

        decimal.GetBits(value, bits);
        MemoryMarshal.AsBytes(bits).CopyTo(place);
    }

    public static void WriteGuid(Span<byte> place, Guid value) => value.TryWriteBytes(place);

    // The instant as its UTC ticks; the offset is not kept.
    public static void WriteDateTimeOffset(Span<byte> place, DateTimeOffset value) => MemoryMarshal.Write(place, value.UtcTicks);

    public static void WriteDateOnly(Span<byte> place, DateOnly value) => MemoryMarshal.Write(place, value.DayNumber);

    public static void WriteTimeOnly(Span<byte> place, TimeOnly value) => MemoryMarshal.Write(place, value.Ticks);

    public static void WriteTimeSpan(Span<byte> place, TimeSpan value) => MemoryMarshal.Write(place, value.Ticks);

    // Reads back what the members above wrote, from a place at the width of the value.
    // Inlined, so that the switch on the width lies in RootReader.GetInteger as it did before the stored
    // form moved here (JIT listing at full optimization; RootReader.GetInteger says why that stays out of
    // line).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadInteger(ReadOnlySpan<byte> place) => place.Length switch
    {
        sizeof(sbyte) => (sbyte)place[0],
        sizeof(short) => MemoryMarshal.Read<short>(place),
        sizeof(int) => MemoryMarshal.Read<int>(place),
        _ => MemoryMarshal.Read<long>(place),
    };

    // Inlined into RootReader.GetUnsigned, as ReadInteger is (see there).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong ReadUnsigned(ReadOnlySpan<byte> place) => place.Length switch
    {
        sizeof(byte) => place[0],
        sizeof(ushort) => MemoryMarshal.Read<ushort>(place),
        sizeof(uint) => MemoryMarshal.Read<uint>(place),
        _ => MemoryMarshal.Read<ulong>(place),
    };

    public static bool ReadBoolean(ReadOnlySpan<byte> place) => place[0] != 0;

    public static float ReadFloat32(ReadOnlySpan<byte> place) => BitConverter.Int32BitsToSingle(MemoryMarshal.Read<int>(place));

    public static double ReadFloat64(ReadOnlySpan<byte> place) => BitConverter.Int64BitsToDouble(MemoryMarshal.Read<long>(place));

    public static decimal ReadDecimal(ReadOnlySpan<byte> place)
    {
        Span<int> bits = stackalloc int[4];

        place[..16].CopyTo(MemoryMarshal.AsBytes(bits));

        return new decimal(bits);
    }

    public static Guid ReadGuid(ReadOnlySpan<byte> place) => new(place[..16]);

    // The stored instant, with offset zero.
    public static DateTimeOffset ReadDateTimeOffset(ReadOnlySpan<byte> place) => new(MemoryMarshal.Read<long>(place), TimeSpan.Zero);

    public static DateOnly ReadDateOnly(ReadOnlySpan<byte> place) => DateOnly.FromDayNumber(MemoryMarshal.Read<int>(place));

    public static TimeOnly ReadTimeOnly(ReadOnlySpan<byte> place) => new(MemoryMarshal.Read<long>(place));

    public static TimeSpan ReadTimeSpan(ReadOnlySpan<byte> place) => new(MemoryMarshal.Read<long>(place));
}
