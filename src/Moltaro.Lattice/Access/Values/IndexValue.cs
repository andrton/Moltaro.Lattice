using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Values;

// One value of an index key or of a bound as a caller gave it, held without a box: the field type
// whose .NET type the value has, its bits and, for text, the string (the remarks of IndexKey). It is
// what lets IndexKey and IndexBound be small values that a caller makes without allocating.
//
// A value of one of the .NET types of the scalar field types, or a Nullable of one, is taken apart by
// its type in Of, and boxes nothing: the generic method is compiled for each value type it is called
// with, and its tests of typeof(T) are decided when it is compiled, as for a primary key given to a set
// (Engine.Sets.KeyArgument). A value given as an object is unboxed, not boxed again. A value of any other
// type is kept as the object it is, boxed if it is a value type: the cost of a value that is about to be
// refused, since no part of an index has its type.
//
// The bits are the .NET value as it lies in memory, written into the 16 bytes from _low on in the byte
// order of the machine: one to eight bytes of a Boolean, an integer, a floating-point value, a DateOnly,
// a TimeOnly or a TimeSpan, the 16 of a Decimal, a Guid or a DateTimeOffset. They are read back the same
// way, so the value is the one given, bit for bit, a negative zero and a NaN payload included.
//
// Bind is where a value meets the schema, by the rules FieldValues applies to a value given as an
// object: the type of the field exactly, or an integer of another width whose value the field type
// holds, and text that is well-formed; anything else is the ArgumentException of FieldValues, with its
// message. The extended allowlist of the milestone contract ("Index preparation and traversal": exact
// integer to Decimal and to floating point, exact Float32 and Float64 widening) is not taken here yet.
[StructLayout(LayoutKind.Sequential)]
internal readonly struct IndexValue
{
    // The bits; see the struct.
    private readonly ulong _low;

    private readonly ulong _high;

    // The string of a String, or the object of a type that no scalar field has; null otherwise.
    private readonly object? _reference;

    // The field type whose .NET type the value has; 0, which no field type has, for null and for an
    // object of another type.
    private readonly LatticeFieldTypeEnum _type;

    private IndexValue(LatticeFieldTypeEnum type, ulong low, ulong high, object? reference)
    {
        _type = type;
        _low = low;
        _high = high;
        _reference = reference;
    }

    // The field type whose .NET type the value has; 0 for null and for a value of another type.
    public LatticeFieldTypeEnum Type => _type;

    // Whether the value is null: given as null, or as a Nullable without a value.
    public bool IsNull => _type == 0 && _reference is null;

    // The text of a String value; null for any other.
    public string? Text => _type == LatticeFieldTypeEnum.String ? (string)_reference! : null;

    // The value of one of the eight integer types, widened without a change of value.
    public Int128 Integer => _type switch
    {
        LatticeFieldTypeEnum.Int8 => As<sbyte>(),
        LatticeFieldTypeEnum.UInt8 => As<byte>(),
        LatticeFieldTypeEnum.Int16 => As<short>(),
        LatticeFieldTypeEnum.UInt16 => As<ushort>(),
        LatticeFieldTypeEnum.Int32 => As<int>(),
        LatticeFieldTypeEnum.UInt32 => As<uint>(),
        LatticeFieldTypeEnum.Int64 => As<long>(),
        LatticeFieldTypeEnum.UInt64 => As<ulong>(),
        _ => throw new InvalidOperationException($"A value of {TypeName} is not an integer."),
    };

    // The name of the .NET type of the value, for a message.
    private string TypeName => _type == 0 ? _reference?.GetType().Name ?? "null" : FieldTypes.ValueType(_type).Name;

    // The value given, without a box when its type is one of the scalar field types or a Nullable of
    // one (see the struct).
    public static IndexValue Of<T>(T value)
    {
        // A Nullable without a value, and a null reference.
        if (value is null)
        {
            return default;
        }

        if (typeof(T) == typeof(string))
        {
            // A reference type: the conversion through object is no box.
            return new IndexValue(LatticeFieldTypeEnum.String, 0, 0, (string)(object)value);
        }

        // A value of a scalar field type or a Nullable of one that has a value; otherwise an object,
        // which is not boxed again, or a value of a type no scalar field has, which is. The tests take
        // the value by reference and do not change it, so it is still not null for OfObject.
        return Is<T, bool>(ref value, LatticeFieldTypeEnum.Boolean, out var result)
            || Is<T, sbyte>(ref value, LatticeFieldTypeEnum.Int8, out result)
            || Is<T, byte>(ref value, LatticeFieldTypeEnum.UInt8, out result)
            || Is<T, short>(ref value, LatticeFieldTypeEnum.Int16, out result)
            || Is<T, ushort>(ref value, LatticeFieldTypeEnum.UInt16, out result)
            || Is<T, int>(ref value, LatticeFieldTypeEnum.Int32, out result)
            || Is<T, uint>(ref value, LatticeFieldTypeEnum.UInt32, out result)
            || Is<T, long>(ref value, LatticeFieldTypeEnum.Int64, out result)
            || Is<T, ulong>(ref value, LatticeFieldTypeEnum.UInt64, out result)
            || Is<T, float>(ref value, LatticeFieldTypeEnum.Float32, out result)
            || Is<T, double>(ref value, LatticeFieldTypeEnum.Float64, out result)
            || Is<T, decimal>(ref value, LatticeFieldTypeEnum.Decimal, out result)
            || Is<T, Guid>(ref value, LatticeFieldTypeEnum.Guid, out result)
            || Is<T, DateTimeOffset>(ref value, LatticeFieldTypeEnum.DateTimeOffset, out result)
            || Is<T, DateOnly>(ref value, LatticeFieldTypeEnum.DateOnly, out result)
            || Is<T, TimeOnly>(ref value, LatticeFieldTypeEnum.TimeOnly, out result)
            || Is<T, TimeSpan>(ref value, LatticeFieldTypeEnum.TimeSpan, out result)
                ? result
                : OfObject(value!);
    }

    // The value as the field holds it: the value itself when its type is the .NET type of the field,
    // an integer of another width converted when the field type holds it, after the rules of
    // FieldValues.NormalizeArgument (see the struct). The value is not null. ArgumentException, for the
    // parameter named, with the message FieldValues gives for a value it refuses.
    public IndexValue Bind(LatticeField field, string parameterName)
    {
        var expected = field.Declaration.ScalarType;

        if (_type == expected)
        {
            if (_type == LatticeFieldTypeEnum.String)
            {
                FieldValues.ThrowIfNotWellFormed(field, (string)_reference!, parameterName);
            }

            return this;
        }

        if (FieldTypes.IsInteger(expected) && FieldTypes.IsInteger(_type) && IntegerRange.Of(expected).Contains(Integer))
        {
            return OfInteger(expected, Integer);
        }

        throw new ArgumentException(
            $"Field '{field.Name}' is {field.Declaration.Type}; the {TypeName} value '{ToObject()}' cannot be used for it.",
            parameterName);
    }

    // The value of a fixed-width type, read back from the bits.
    public TValue As<TValue>()
        where TValue : unmanaged
    {
        Span<ulong> bits = [_low, _high];

        return MemoryMarshal.Read<TValue>(MemoryMarshal.AsBytes(bits));
    }

    // The value as an object, boxed when it is of a value type: for IndexBound.Value and for a message,
    // never on the path of a search. Null for null.
    public object? ToObject() => _type switch
    {
        0 => _reference,
        LatticeFieldTypeEnum.String => _reference,
        LatticeFieldTypeEnum.Boolean => As<bool>(),
        LatticeFieldTypeEnum.Int8 => As<sbyte>(),
        LatticeFieldTypeEnum.UInt8 => As<byte>(),
        LatticeFieldTypeEnum.Int16 => As<short>(),
        LatticeFieldTypeEnum.UInt16 => As<ushort>(),
        LatticeFieldTypeEnum.Int32 => As<int>(),
        LatticeFieldTypeEnum.UInt32 => As<uint>(),
        LatticeFieldTypeEnum.Int64 => As<long>(),
        LatticeFieldTypeEnum.UInt64 => As<ulong>(),
        LatticeFieldTypeEnum.Float32 => As<float>(),
        LatticeFieldTypeEnum.Float64 => As<double>(),
        LatticeFieldTypeEnum.Decimal => As<decimal>(),
        LatticeFieldTypeEnum.Guid => As<Guid>(),
        LatticeFieldTypeEnum.DateTimeOffset => As<DateTimeOffset>(),
        LatticeFieldTypeEnum.DateOnly => As<DateOnly>(),
        LatticeFieldTypeEnum.TimeOnly => As<TimeOnly>(),
        _ => As<TimeSpan>(),
    };

    // Whether T is TValue or a Nullable of it, with the value, of the field type given, when it is. The
    // value is not null. Decided when the method is compiled for T and TValue, so a test that fails
    // costs nothing for a value type T.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Is<T, TValue>(ref T value, LatticeFieldTypeEnum type, out IndexValue result)
        where TValue : unmanaged
    {
        if (typeof(T) == typeof(TValue))
        {
            result = Fixed(type, Unsafe.As<T, TValue>(ref value));

            return true;
        }

        if (typeof(T) == typeof(TValue?))
        {
            result = Fixed(type, Unsafe.As<T, TValue?>(ref value).GetValueOrDefault());

            return true;
        }

        result = default;

        return false;
    }

    private static IndexValue Fixed<TValue>(LatticeFieldTypeEnum type, TValue value)
        where TValue : unmanaged
    {
        Span<ulong> bits = [0, 0];

        MemoryMarshal.Write(MemoryMarshal.AsBytes(bits), in value);

        return new IndexValue(type, bits[0], bits[1], null);
    }

    // An integer of the type given, whose range holds it.
    private static IndexValue OfInteger(LatticeFieldTypeEnum type, Int128 value) => type switch
    {
        LatticeFieldTypeEnum.Int8 => Fixed(type, (sbyte)value),
        LatticeFieldTypeEnum.UInt8 => Fixed(type, (byte)value),
        LatticeFieldTypeEnum.Int16 => Fixed(type, (short)value),
        LatticeFieldTypeEnum.UInt16 => Fixed(type, (ushort)value),
        LatticeFieldTypeEnum.Int32 => Fixed(type, (int)value),
        LatticeFieldTypeEnum.UInt32 => Fixed(type, (uint)value),
        LatticeFieldTypeEnum.Int64 => Fixed(type, (long)value),
        _ => Fixed(type, (ulong)value),
    };

    // A value held as an object: unboxed when it is of a scalar field type.
    private static IndexValue OfObject(object value) => value switch
    {
        string text => new IndexValue(LatticeFieldTypeEnum.String, 0, 0, text),
        bool boolean => Fixed(LatticeFieldTypeEnum.Boolean, boolean),
        sbyte integer => Fixed(LatticeFieldTypeEnum.Int8, integer),
        byte integer => Fixed(LatticeFieldTypeEnum.UInt8, integer),
        short integer => Fixed(LatticeFieldTypeEnum.Int16, integer),
        ushort integer => Fixed(LatticeFieldTypeEnum.UInt16, integer),
        int integer => Fixed(LatticeFieldTypeEnum.Int32, integer),
        uint integer => Fixed(LatticeFieldTypeEnum.UInt32, integer),
        long integer => Fixed(LatticeFieldTypeEnum.Int64, integer),
        ulong integer => Fixed(LatticeFieldTypeEnum.UInt64, integer),
        float number => Fixed(LatticeFieldTypeEnum.Float32, number),
        double number => Fixed(LatticeFieldTypeEnum.Float64, number),
        decimal number => Fixed(LatticeFieldTypeEnum.Decimal, number),
        Guid guid => Fixed(LatticeFieldTypeEnum.Guid, guid),
        DateTimeOffset instant => Fixed(LatticeFieldTypeEnum.DateTimeOffset, instant),
        DateOnly date => Fixed(LatticeFieldTypeEnum.DateOnly, date),
        TimeOnly time => Fixed(LatticeFieldTypeEnum.TimeOnly, time),
        TimeSpan span => Fixed(LatticeFieldTypeEnum.TimeSpan, span),
        _ => new IndexValue(0, 0, 0, value),
    };
}
