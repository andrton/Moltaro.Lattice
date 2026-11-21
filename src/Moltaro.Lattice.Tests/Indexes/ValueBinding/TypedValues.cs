namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// A key or a bound of a value that a theory holds as an object, made with the value given as its own .NET
// type, as an application gives it: the generic factory is called with that type, never with object.
internal static class TypedValues
{
    public static IndexKey Equal(object value) => value switch
    {
        bool given => IndexKey.Equal(given),
        sbyte given => IndexKey.Equal(given),
        byte given => IndexKey.Equal(given),
        short given => IndexKey.Equal(given),
        ushort given => IndexKey.Equal(given),
        int given => IndexKey.Equal(given),
        uint given => IndexKey.Equal(given),
        long given => IndexKey.Equal(given),
        ulong given => IndexKey.Equal(given),
        float given => IndexKey.Equal(given),
        double given => IndexKey.Equal(given),
        decimal given => IndexKey.Equal(given),
        Guid given => IndexKey.Equal(given),
        DateTimeOffset given => IndexKey.Equal(given),
        DateOnly given => IndexKey.Equal(given),
        TimeOnly given => IndexKey.Equal(given),
        TimeSpan given => IndexKey.Equal(given),
        string given => IndexKey.Equal(given),
        DateTime given => IndexKey.Equal(given),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No key is made here for a value of this type."),
    };

    public static IndexBound Bound(object value, bool inclusive) => value switch
    {
        bool given => Bound(given, inclusive),
        sbyte given => Bound(given, inclusive),
        byte given => Bound(given, inclusive),
        short given => Bound(given, inclusive),
        ushort given => Bound(given, inclusive),
        int given => Bound(given, inclusive),
        uint given => Bound(given, inclusive),
        long given => Bound(given, inclusive),
        ulong given => Bound(given, inclusive),
        float given => Bound(given, inclusive),
        double given => Bound(given, inclusive),
        decimal given => Bound(given, inclusive),
        Guid given => Bound(given, inclusive),
        DateTimeOffset given => Bound(given, inclusive),
        DateOnly given => Bound(given, inclusive),
        TimeOnly given => Bound(given, inclusive),
        TimeSpan given => Bound(given, inclusive),
        string given => Bound(given, inclusive),
        DateTime given => Bound(given, inclusive),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "No bound is made here for a value of this type."),
    };

    private static IndexBound Bound<T>(T value, bool inclusive)
        where T : notnull =>
        inclusive ? IndexBound.Inclusive(value) : IndexBound.Exclusive(value);
}
