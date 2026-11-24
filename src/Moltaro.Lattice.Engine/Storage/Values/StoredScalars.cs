using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;

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
// of its place, which the caller made wide enough for it. The generic and the boxed forms at the end serve
// the values a record holds inside a List and an owned value, by the same writers and readers.
internal static class StoredScalars
{
    // Writes a non-null probe in the same form as the stored value. Binding has already converted
    // the probe to the declaration's scalar type, including the underlying integer of an enum.
    public static void WriteBound(Span<byte> place, StoredTypeEnum type, in IndexValue value)
    {
        switch (type)
        {
            case StoredTypeEnum.Boolean:
                WriteBoolean(place, value.As<bool>());
                break;
            case StoredTypeEnum.SignedInteger:
                WriteInteger(place, (long)value.Integer);
                break;
            case StoredTypeEnum.UnsignedInteger:
                WriteUnsigned(place, (ulong)value.Integer);
                break;
            case StoredTypeEnum.Float32:
                WriteFloat32(place, value.As<float>());
                break;
            case StoredTypeEnum.Float64:
                WriteFloat64(place, value.As<double>());
                break;
            case StoredTypeEnum.Decimal:
                WriteDecimal(place, value.As<decimal>());
                break;
            case StoredTypeEnum.Guid:
                WriteGuid(place, value.As<Guid>());
                break;
            case StoredTypeEnum.DateTimeOffset:
                WriteDateTimeOffset(place, value.As<DateTimeOffset>());
                break;
            case StoredTypeEnum.DateOnly:
                WriteDateOnly(place, value.As<DateOnly>());
                break;
            case StoredTypeEnum.TimeOnly:
                WriteTimeOnly(place, value.As<TimeOnly>());
                break;
            case StoredTypeEnum.TimeSpan:
                WriteTimeSpan(place, value.As<TimeSpan>());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Not a fixed-width type.");
        }
    }

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

    // Writes a value of the fixed-width .NET type TValue that a record holds for a scalar of the schema, an
    // Enum as its integer and a Reference as its key, by the writer of that type above. Each test of the type
    // is decided when the method is compiled for TValue, so the value is never boxed: for the elements of a
    // packed List (PackedScalars).
    public static void WriteValue<TValue>(Span<byte> place, TValue value)
        where TValue : struct
    {
        if (typeof(TValue) == typeof(bool))
        {
            WriteBoolean(place, Unsafe.As<TValue, bool>(ref value));
        }
        else if (typeof(TValue) == typeof(sbyte))
        {
            WriteInteger(place, Unsafe.As<TValue, sbyte>(ref value));
        }
        else if (typeof(TValue) == typeof(short))
        {
            WriteInteger(place, Unsafe.As<TValue, short>(ref value));
        }
        else if (typeof(TValue) == typeof(int))
        {
            WriteInteger(place, Unsafe.As<TValue, int>(ref value));
        }
        else if (typeof(TValue) == typeof(long))
        {
            WriteInteger(place, Unsafe.As<TValue, long>(ref value));
        }
        else if (typeof(TValue) == typeof(byte))
        {
            WriteUnsigned(place, Unsafe.As<TValue, byte>(ref value));
        }
        else if (typeof(TValue) == typeof(ushort))
        {
            WriteUnsigned(place, Unsafe.As<TValue, ushort>(ref value));
        }
        else if (typeof(TValue) == typeof(uint))
        {
            WriteUnsigned(place, Unsafe.As<TValue, uint>(ref value));
        }
        else if (typeof(TValue) == typeof(ulong))
        {
            WriteUnsigned(place, Unsafe.As<TValue, ulong>(ref value));
        }
        else if (typeof(TValue) == typeof(float))
        {
            WriteFloat32(place, Unsafe.As<TValue, float>(ref value));
        }
        else if (typeof(TValue) == typeof(double))
        {
            WriteFloat64(place, Unsafe.As<TValue, double>(ref value));
        }
        else if (typeof(TValue) == typeof(decimal))
        {
            WriteDecimal(place, Unsafe.As<TValue, decimal>(ref value));
        }
        else if (typeof(TValue) == typeof(Guid))
        {
            WriteGuid(place, Unsafe.As<TValue, Guid>(ref value));
        }
        else if (typeof(TValue) == typeof(DateTimeOffset))
        {
            WriteDateTimeOffset(place, Unsafe.As<TValue, DateTimeOffset>(ref value));
        }
        else if (typeof(TValue) == typeof(DateOnly))
        {
            WriteDateOnly(place, Unsafe.As<TValue, DateOnly>(ref value));
        }
        else if (typeof(TValue) == typeof(TimeOnly))
        {
            WriteTimeOnly(place, Unsafe.As<TValue, TimeOnly>(ref value));
        }
        else if (typeof(TValue) == typeof(TimeSpan))
        {
            WriteTimeSpan(place, Unsafe.As<TValue, TimeSpan>(ref value));
        }
        else
        {
            throw new UnreachableException($"{typeof(TValue).Name} is the type of no scalar of the schema.");
        }
    }

    // Reads back what WriteValue wrote, as a value of TValue, without a box.
    public static TValue ReadValue<TValue>(ReadOnlySpan<byte> place)
        where TValue : struct
    {
        if (typeof(TValue) == typeof(bool))
        {
            return As<bool, TValue>(ReadBoolean(place));
        }

        if (typeof(TValue) == typeof(sbyte))
        {
            return As<sbyte, TValue>((sbyte)ReadInteger(place));
        }

        if (typeof(TValue) == typeof(short))
        {
            return As<short, TValue>((short)ReadInteger(place));
        }

        if (typeof(TValue) == typeof(int))
        {
            return As<int, TValue>((int)ReadInteger(place));
        }

        if (typeof(TValue) == typeof(long))
        {
            return As<long, TValue>(ReadInteger(place));
        }

        if (typeof(TValue) == typeof(byte))
        {
            return As<byte, TValue>((byte)ReadUnsigned(place));
        }

        if (typeof(TValue) == typeof(ushort))
        {
            return As<ushort, TValue>((ushort)ReadUnsigned(place));
        }

        if (typeof(TValue) == typeof(uint))
        {
            return As<uint, TValue>((uint)ReadUnsigned(place));
        }

        if (typeof(TValue) == typeof(ulong))
        {
            return As<ulong, TValue>(ReadUnsigned(place));
        }

        if (typeof(TValue) == typeof(float))
        {
            return As<float, TValue>(ReadFloat32(place));
        }

        if (typeof(TValue) == typeof(double))
        {
            return As<double, TValue>(ReadFloat64(place));
        }

        if (typeof(TValue) == typeof(decimal))
        {
            return As<decimal, TValue>(ReadDecimal(place));
        }

        if (typeof(TValue) == typeof(Guid))
        {
            return As<Guid, TValue>(ReadGuid(place));
        }

        if (typeof(TValue) == typeof(DateTimeOffset))
        {
            return As<DateTimeOffset, TValue>(ReadDateTimeOffset(place));
        }

        if (typeof(TValue) == typeof(DateOnly))
        {
            return As<DateOnly, TValue>(ReadDateOnly(place));
        }

        if (typeof(TValue) == typeof(TimeOnly))
        {
            return As<TimeOnly, TValue>(ReadTimeOnly(place));
        }

        if (typeof(TValue) == typeof(TimeSpan))
        {
            return As<TimeSpan, TValue>(ReadTimeSpan(place));
        }

        throw new UnreachableException($"{typeof(TValue).Name} is the type of no scalar of the schema.");
    }

    // Writes a scalar given boxed in the .NET type a record holds for it, by the writer of its type: for a
    // field of an owned value, whose values a LatticeOwnedValue holds boxed.
    public static void WriteBoxed(Span<byte> place, object value)
    {
        switch (value)
        {
            case bool boolean:
                WriteBoolean(place, boolean);
                break;
            case sbyte integer:
                WriteInteger(place, integer);
                break;
            case short integer:
                WriteInteger(place, integer);
                break;
            case int integer:
                WriteInteger(place, integer);
                break;
            case long integer:
                WriteInteger(place, integer);
                break;
            case byte integer:
                WriteUnsigned(place, integer);
                break;
            case ushort integer:
                WriteUnsigned(place, integer);
                break;
            case uint integer:
                WriteUnsigned(place, integer);
                break;
            case ulong integer:
                WriteUnsigned(place, integer);
                break;
            case float number:
                WriteFloat32(place, number);
                break;
            case double number:
                WriteFloat64(place, number);
                break;
            case decimal number:
                WriteDecimal(place, number);
                break;
            case Guid guid:
                WriteGuid(place, guid);
                break;
            case DateTimeOffset instant:
                WriteDateTimeOffset(place, instant);
                break;
            case DateOnly date:
                WriteDateOnly(place, date);
                break;
            case TimeOnly time:
                WriteTimeOnly(place, time);
                break;
            case TimeSpan span:
                WriteTimeSpan(place, span);
                break;
            default:
                throw new UnreachableException($"A {value.GetType().Name} is the value of no scalar of the schema.");
        }
    }

    // Reads back what WriteBoxed wrote, boxed in the .NET type of the scalar type given, as a record holds it:
    // the type of the field, the underlying integer of an Enum and the key of the target of a Reference.
    public static object ReadBoxed(ReadOnlySpan<byte> place, LatticeFieldTypeEnum scalarType) => scalarType switch
    {
        LatticeFieldTypeEnum.Boolean => ReadBoolean(place),
        LatticeFieldTypeEnum.Int8 => (sbyte)ReadInteger(place),
        LatticeFieldTypeEnum.Int16 => (short)ReadInteger(place),
        LatticeFieldTypeEnum.Int32 => (int)ReadInteger(place),
        LatticeFieldTypeEnum.Int64 => ReadInteger(place),
        LatticeFieldTypeEnum.UInt8 => (byte)ReadUnsigned(place),
        LatticeFieldTypeEnum.UInt16 => (ushort)ReadUnsigned(place),
        LatticeFieldTypeEnum.UInt32 => (uint)ReadUnsigned(place),
        LatticeFieldTypeEnum.UInt64 => ReadUnsigned(place),
        LatticeFieldTypeEnum.Float32 => ReadFloat32(place),
        LatticeFieldTypeEnum.Float64 => ReadFloat64(place),
        LatticeFieldTypeEnum.Decimal => ReadDecimal(place),
        LatticeFieldTypeEnum.Guid => ReadGuid(place),
        LatticeFieldTypeEnum.DateTimeOffset => ReadDateTimeOffset(place),
        LatticeFieldTypeEnum.DateOnly => ReadDateOnly(place),
        LatticeFieldTypeEnum.TimeOnly => ReadTimeOnly(place),
        LatticeFieldTypeEnum.TimeSpan => ReadTimeSpan(place),
        _ => throw new UnreachableException($"{scalarType} is no scalar type of a fixed width."),
    };

    // A value of one type as the same value of another, without a box: the caller has tested that TFrom is TTo.
    private static TTo As<TFrom, TTo>(TFrom value) => Unsafe.As<TFrom, TTo>(ref value);
}
