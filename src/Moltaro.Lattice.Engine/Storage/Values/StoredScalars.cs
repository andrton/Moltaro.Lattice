using Moltaro.Lattice.Access.Values;

namespace Moltaro.Lattice.Storage.Values;

// The bytes a root stores for a value of a fixed-width type, written and read in this one place: the
// root writer writes a field through it, the search key of an index writes the values of a key through
// it, and the root reader and the order of an index read them back through it (milestone contract,
// "Native layout and allocation"). An entry of an index takes the bytes of its fixed parts from its
// root as they lie and does not write them again (Ordered.IndexLayout.WriteEntry). Not built yet: the
// Set… members of RootWriter and the Get… members of RootReader still write and read these forms
// themselves, and move onto this type before anything of an index writes a value, so that no value is
// written in two forms.
//
// A place is the span of the value at its width, which the caller takes from the layout: 1, 2, 4 or 8
// bytes for an integer, as its type has; 1 for a Boolean, 0 or 1; 4 for a Float32 and a DateOnly, its
// bits and its day number; 8 for a Float64, its bits, and for a DateTimeOffset, a TimeOnly and a
// TimeSpan, their UTC ticks, ticks since midnight and signed ticks; 16 for a Decimal, the four integers
// of decimal.GetBits, and for a Guid, its bytes in the order of Guid.TryWriteBytes. Every number is in
// the byte order of the machine, as everything in native memory is. Nothing here allocates, checks a
// range or reads the schema: the callers have checked their values.
internal static class StoredScalars
{
    // Writes an integer, signed or unsigned, at the width of the place.
    public static void WriteInteger(Span<byte> place, long value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static void WriteUnsigned(Span<byte> place, ulong value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static void WriteBoolean(Span<byte> place, bool value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    // The bits as they are, those of a NaN and the sign of a zero included.
    public static void WriteFloat32(Span<byte> place, float value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static void WriteFloat64(Span<byte> place, double value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    // The four integers of decimal.GetBits, so that the scale and the sign are kept.
    public static void WriteDecimal(Span<byte> place, decimal value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static void WriteGuid(Span<byte> place, Guid value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    // The instant as its UTC ticks; the offset is not kept.
    public static void WriteDateTimeOffset(Span<byte> place, DateTimeOffset value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static void WriteDateOnly(Span<byte> place, DateOnly value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static void WriteTimeOnly(Span<byte> place, TimeOnly value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static void WriteTimeSpan(Span<byte> place, TimeSpan value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    // Writes a value of an index key, bound to its field (Access.Sets.IndexCriteria), in the form its
    // stored type has, by the members above: what the search key of an index writes for each fixed part.
    public static void Write(Span<byte> place, StoredTypeEnum type, in IndexValue value) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    // Reads back what the members above wrote, from a place at the width of the value.
    public static long ReadInteger(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static ulong ReadUnsigned(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static bool ReadBoolean(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static float ReadFloat32(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static double ReadFloat64(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static decimal ReadDecimal(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static Guid ReadGuid(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    // The stored instant, with offset zero.
    public static DateTimeOffset ReadDateTimeOffset(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static DateOnly ReadDateOnly(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static TimeOnly ReadTimeOnly(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");

    public static TimeSpan ReadTimeSpan(ReadOnlySpan<byte> place) =>
        throw new NotSupportedException("The stored form of a value is not built yet.");
}
