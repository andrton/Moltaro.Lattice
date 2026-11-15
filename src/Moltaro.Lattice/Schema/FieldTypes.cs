namespace Moltaro.Lattice.Schema;

// What the schema field types mean in .NET, and which of them may stand where.
internal static class FieldTypes
{
    public static Type ValueType(LatticeFieldTypeEnum type) => type switch
    {
        LatticeFieldTypeEnum.Boolean => typeof(bool),
        LatticeFieldTypeEnum.Int8 => typeof(sbyte),
        LatticeFieldTypeEnum.UInt8 => typeof(byte),
        LatticeFieldTypeEnum.Int16 => typeof(short),
        LatticeFieldTypeEnum.UInt16 => typeof(ushort),
        LatticeFieldTypeEnum.Int32 => typeof(int),
        LatticeFieldTypeEnum.UInt32 => typeof(uint),
        LatticeFieldTypeEnum.Int64 => typeof(long),
        LatticeFieldTypeEnum.UInt64 => typeof(ulong),
        LatticeFieldTypeEnum.Float32 => typeof(float),
        LatticeFieldTypeEnum.Float64 => typeof(double),
        LatticeFieldTypeEnum.Decimal => typeof(decimal),
        LatticeFieldTypeEnum.String => typeof(string),
        LatticeFieldTypeEnum.Guid => typeof(Guid),
        LatticeFieldTypeEnum.DateTimeOffset => typeof(DateTimeOffset),
        LatticeFieldTypeEnum.DateOnly => typeof(DateOnly),
        LatticeFieldTypeEnum.TimeOnly => typeof(TimeOnly),
        LatticeFieldTypeEnum.TimeSpan => typeof(TimeSpan),
        LatticeFieldTypeEnum.Binary => typeof(byte[]),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool IsInteger(LatticeFieldTypeEnum type) =>
        type is >= LatticeFieldTypeEnum.Int8 and <= LatticeFieldTypeEnum.UInt64;

    public static bool CanBeKey(LatticeFieldTypeEnum type) =>
        IsInteger(type) || type is LatticeFieldTypeEnum.String or LatticeFieldTypeEnum.Guid;

    // The types whose values have an equality of their own: a scalar that is not Binary, an
    // Enum, a Reference. Only they may be a Map key, a Set element or the end of an index
    // part; an owned value, a collection and a byte array have no equality the schema
    // declares.
    public static bool HasEquality(LatticeFieldTypeEnum type) =>
        type is >= LatticeFieldTypeEnum.Boolean and <= LatticeFieldTypeEnum.TimeSpan
            or LatticeFieldTypeEnum.Enum
            or LatticeFieldTypeEnum.Reference;

    // The types that hold other values: an owned value and the three collections.
    public static bool IsNested(LatticeFieldTypeEnum type) =>
        type is LatticeFieldTypeEnum.Owned or LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set;
}
