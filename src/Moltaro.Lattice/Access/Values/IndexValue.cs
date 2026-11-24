using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Values;

// One value of an index key or of a bound as a caller gave it, held without a box: the field type
// whose .NET type the value has, its bits and, for text, the string, and for a null the type it was
// given as (the remarks of IndexKey). It is what lets IndexKey and IndexBound be small values that a
// caller makes without allocating.
//
// A value of one of the .NET types of the scalar field types, or a Nullable of one, is taken apart by
// its type in Of, and boxes nothing: the generic method is compiled for each value type it is called
// with, and its tests of typeof(T) are decided when it is compiled, as for a primary key given to a set
// (Engine.Sets.KeyArgument); a value of a value type is reached only through Unsafe.As, never tested
// against null as an object, so a build whose code the JIT does not optimize boxes nothing either. A
// value given as an object is unboxed, not boxed again. A value of any other type is kept as the object
// it is, boxed if it is a value type: the cost of a value that is about to be refused, since no part of
// an index has its type.
//
// The bits are the .NET value as it lies in memory, written into the 16 bytes from _low on in the byte
// order of the machine: one to eight bytes of a Boolean, an integer, a floating-point value, a DateOnly,
// a TimeOnly or a TimeSpan, the 16 of a Decimal, a Guid or a DateTimeOffset. They are read back the same
// way, so the value is the one given, bit for bit, a negative zero and a NaN payload included.
//
// Bind is where a value of an index key or of a bound meets the schema: it applies the remarks of
// IndexKey and the allowlist of Q07.3.6 of docs/decisions/queries.md. Beyond them, a converted value is
// written into the bits as any other, so it is not boxed; the value of an Enum part is refused in the
// words of the validation of a write (EnumValues.Undeclared); and a value it cannot use is refused with
// the message of a field value (FieldValues.CannotBeUsed). The allowlist of numbers alone, TryConvertTo,
// is also the rule by which a record and an owned value take the value of a field (FieldValues.Normalize),
// so that a number means the same in a key and in an entity.
[StructLayout(LayoutKind.Sequential)]
internal readonly struct IndexValue
{
    // The type of a null given as a type that no scalar field has, a Nullable of one or a reference type
    // other than string and object, which is no field type: such a null is refused for every part, as a
    // value of its type is.
    private const LatticeFieldTypeEnum OtherType = (LatticeFieldTypeEnum)byte.MaxValue;

    // The bits; see the struct.
    private readonly ulong _low;

    private readonly ulong _high;

    // The string of a String, or the object of a type that no scalar field has; null otherwise.
    private readonly object? _reference;

    // The field type whose .NET type the value has; 0, which no field type has, for null and for an
    // object of another type.
    private readonly LatticeFieldTypeEnum _type;

    // For a null, the field type of the .NET type it was given as, or OtherType; 0 for a null given as an
    // object, which has no type, and for a value. It lies in the padding after _type: a value is 32 bytes
    // with it as without it.
    private readonly LatticeFieldTypeEnum _typeOfNull;

    private IndexValue(LatticeFieldTypeEnum type, ulong low, ulong high, object? reference)
    {
        _type = type;
        _low = low;
        _high = high;
        _reference = reference;
    }

    // A null given as a value of the field type given; see NullOf.
    private IndexValue(LatticeFieldTypeEnum typeOfNull)
    {
        _typeOfNull = typeOfNull;
    }

    // The field type whose .NET type the value has; 0 for null and for a value of another type.
    public LatticeFieldTypeEnum Type => _type;

    // Whether the value is null: given as null, as a null string or as a Nullable without a value.
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
        // A reference type, whose conversion through object is no box: a null reference is the null of a
        // string, one without a type given as an object, or one of a type no scalar field has; a string is
        // a String; any other object is taken by its type (OfObject). A value type is not tested here,
        // where an unoptimized build would box it.
        if (!typeof(T).IsValueType)
        {
            if (value is null)
            {
                if (typeof(T) == typeof(string))
                {
                    return NullOf(LatticeFieldTypeEnum.String);
                }

                return typeof(T) == typeof(object) ? default : NullOf(OtherType);
            }

            if (value is string text)
            {
                return new IndexValue(LatticeFieldTypeEnum.String, 0, 0, text);
            }

            return OfObject(value);
        }

        // A value of a scalar field type or a Nullable of one; otherwise a value of a type no scalar field
        // has, which is boxed, or a Nullable of one without a value (OfOther).
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
                : OfOther(value);
    }

    // The value as the field holds it, by the rules of the struct. The value of the .NET type of the field
    // is taken first, with no call for a fixed-width value of a field that is not an Enum; anything else
    // is BindOther's. ArgumentException, for the parameter named, for a value the rules refuse. A null
    // given as an object is returned as it is: whether the part can be null is the caller's check.
    public IndexValue Bind(LatticeField field, string parameterName)
    {
        var declaration = field.Declaration;

        if (_type == declaration.ScalarType)
        {
            if (_type == LatticeFieldTypeEnum.String)
            {
                FieldValues.ThrowIfNotWellFormed(field, (string)_reference!, parameterName);
            }
            else if (declaration.EnumType is { } enumType)
            {
                ThrowIfNotAllowed(enumType, field, parameterName);
            }

            return this;
        }

        return BindOther(field, parameterName);
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

    // Whether a value of the field type from can be bound to a field of the field type to: a pair of the
    // allowlist, for which TryConvert decides by the value, and by which a null with a type is checked.
    private static bool Pairs(LatticeFieldTypeEnum from, LatticeFieldTypeEnum to)
    {
        if (from == to)
        {
            return true;
        }

        if (FieldTypes.IsInteger(from))
        {
            return FieldTypes.IsInteger(to) || to is LatticeFieldTypeEnum.Decimal or LatticeFieldTypeEnum.Float32 or LatticeFieldTypeEnum.Float64;
        }

        return (from, to) is (LatticeFieldTypeEnum.Float32, LatticeFieldTypeEnum.Float64) or (LatticeFieldTypeEnum.Float64, LatticeFieldTypeEnum.Float32);
    }

    // An integer in the field type given, when it is the same number there: in the range of an integer
    // type; any integer for a Decimal, which holds every one of the eight types; for a Float32 or a Float64
    // one that converts back to itself, which an integer the conversion rounds does not.
    private static bool TryConvertInteger(Int128 integer, LatticeFieldTypeEnum type, out IndexValue converted)
    {
        switch (type)
        {
            case LatticeFieldTypeEnum.Decimal:
                converted = Fixed(type, (decimal)integer);

                return true;
            case LatticeFieldTypeEnum.Float32:
                var single = (float)integer;
                converted = Fixed(type, single);

                return (Int128)single == integer;
            case LatticeFieldTypeEnum.Float64:
                var number = (double)integer;
                converted = Fixed(type, number);

                return (Int128)number == integer;
            default:
                var fits = IntegerRange.Of(type).Contains(integer);
                converted = fits ? OfInteger(type, integer) : default;

                return fits;
        }
    }

    // After the type of the field (Bind): a value of another type converted by the allowlist, and checked
    // when its part is an Enum; a null with a type accepted where a value of its type would be, and one
    // without a type as it is. ArgumentException for anything else.
    private IndexValue BindOther(LatticeField field, string parameterName)
    {
        var declaration = field.Declaration;
        var expected = declaration.ScalarType;

        if (IsNull)
        {
            if (_typeOfNull == 0 || Pairs(_typeOfNull, expected))
            {
                return this;
            }

            var givenAs = _typeOfNull == OtherType ? "a type no field has" : FieldTypes.ValueType(_typeOfNull).Name;

            throw new ArgumentException($"Field '{field.Name}' is {declaration.Type}; a null given as {givenAs} cannot be used for it.", parameterName);
        }

        if (TryConvertTo(expected, out var converted))
        {
            if (declaration.EnumType is { } enumType)
            {
                converted.ThrowIfNotAllowed(enumType, field, parameterName);
            }

            return converted;
        }

        throw FieldValues.CannotBeUsed(field, ToObject()!, parameterName);
    }

    // The value, of another type than the field type given, in that type when the allowlist takes it: a pair
    // of the allowlist (Pairs) and the same number there (TryConvert). A value of the field type itself is
    // taken before this is asked, by Bind and by FieldValues.Normalize. False for anything else, a null and a
    // value of a type no scalar field has included.
    public bool TryConvertTo(LatticeFieldTypeEnum type, out IndexValue converted)
    {
        converted = default;

        return Pairs(_type, type) && TryConvert(type, out converted);
    }

    // The value in the field type given, a pair of the allowlist with its own (Pairs), when it is the same
    // number there.
    private bool TryConvert(LatticeFieldTypeEnum type, out IndexValue converted)
    {
        if (FieldTypes.IsInteger(_type))
        {
            return TryConvertInteger(Integer, type, out converted);
        }

        if (_type == LatticeFieldTypeEnum.Float32)
        {
            // Every Float32 is a Float64: a NaN, an infinity and a zero with its sign as well.
            converted = Fixed(type, (double)As<float>());

            return true;
        }

        // A Float64 for a Float32. The conversion rounds, and it turns a value beyond the range of a
        // Float32 into an infinity and a subnormal Float64 into a zero, none of which converts back to the
        // value given. A NaN, never equal to itself, is taken by its class.
        var number = As<double>();
        var narrowed = (float)number;
        converted = Fixed(type, narrowed);

        return double.IsNaN(number) || narrowed == number;
    }

    // For an integer in the underlying type of the enum type of an Enum part: refused when the enum type
    // does not allow it, in the words of the validation of a write (EnumValues.Undeclared).
    private void ThrowIfNotAllowed(LatticeEnumType enumType, LatticeField field, string parameterName)
    {
        if (!enumType.IsValid(Integer))
        {
            throw new ArgumentException(
                $"Field '{field.Name}' is {field.Declaration.Type}; {EnumValues.Undeclared(enumType, ToObject()!)}, cannot be used for it.",
                parameterName);
        }
    }

    // Whether T is TValue or a Nullable of it, with the value, of the field type given, when it is, and
    // a null of that type for a Nullable without a value. Decided when the method is compiled for T and
    // TValue, so a test that fails costs nothing for a value type T.
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
            result = Unsafe.As<T, TValue?>(ref value) is { } held ? Fixed(type, held) : NullOf(type);

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

    // A value of a value type no scalar field has, boxed (see the struct). A box that is null is a Nullable
    // without a value of such a type.
    private static IndexValue OfOther<T>(T value)
    {
        object? held = value;

        return held is null ? NullOf(OtherType) : OfObject(held);
    }

    // A null given as a value of the field type given, or of OtherType.
    private static IndexValue NullOf(LatticeFieldTypeEnum type) => new(type);

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
