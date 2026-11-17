using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// Values for every field of EveryType, for the tests that a child loads every scalar type as its master
// holds it (Loading) and applies every scalar type as its master writes it (Following). For each type the list holds its extremes and its special values: NaN, both infinities,
// negative zero and the smallest subnormal of the floating types; a Decimal with its scale and a
// negative zero; the first and the last instant, date, time of day and duration; an empty text, text of
// four bytes a character and text long enough to lie in chunks; an empty Binary value, one in a block of
// its own and one in chunks; the members of both enum types. The entity of a number takes the value at
// that number of the list of each type, so that a dozen entities hold every value; a nullable field is
// null in every fourth entity.
//
// Same compares two values read from two instances as they are stored, not as numbers: a floating value
// by its bits, a Decimal by its four integers, so that its scale counts, an instant by its ticks and its
// offset, a Binary value by its bytes; every other value by its type and its equality.
internal static class EveryTypeValues
{
    // Enough entities for the longest list.
    public const int Entities = 12;

    private static readonly string LongText = string.Concat(Enumerable.Repeat("aé€𐐨 ", 15_000));

    private static readonly byte[] BlockBytes = [.. Enumerable.Range(0, 5_000).Select(index => (byte)(index * 7))];

    private static readonly byte[] ChunkedBytes = [.. Enumerable.Range(0, 200_000).Select(index => (byte)(index * 13))];

    public static LatticeRecord RecordOf(LatticeEntityType entityType, int number)
    {
        var record = new LatticeRecord(entityType);

        foreach (var field in entityType.Fields)
        {
            if (field.Name == "Id")
            {
                record[field.Name] = (long)number;
            }
            else if (field.Declaration.IsNullable && number % 4 == 0)
            {
                record[field.Name] = null;
            }
            else
            {
                var values = ValuesOf(field.Declaration);

                record[field.Name] = values[number % values.Length];
            }
        }

        return record;
    }

    public static bool Same(object? expected, object? actual) => (expected, actual) switch
    {
        (null, null) => true,
        (float first, float second) => BitConverter.SingleToInt32Bits(first) == BitConverter.SingleToInt32Bits(second),
        (double first, double second) => BitConverter.DoubleToInt64Bits(first) == BitConverter.DoubleToInt64Bits(second),
        (decimal first, decimal second) => decimal.GetBits(first).AsSpan().SequenceEqual(decimal.GetBits(second)),
        (DateTimeOffset first, DateTimeOffset second) => first.UtcTicks == second.UtcTicks && first.Offset == second.Offset,
        (byte[] first, byte[] second) => first.AsSpan().SequenceEqual(second),
        _ => expected is not null && actual is not null && expected.GetType() == actual.GetType() && expected.Equals(actual),
    };

    private static object[] ValuesOf(LatticeValueDeclaration declaration) => declaration.Type switch
    {
        LatticeFieldTypeEnum.Boolean => [false, true],
        LatticeFieldTypeEnum.Int8 => [sbyte.MinValue, sbyte.MaxValue, (sbyte)0, (sbyte)-7],
        LatticeFieldTypeEnum.UInt8 => [byte.MinValue, byte.MaxValue, (byte)7],
        LatticeFieldTypeEnum.Int16 => [short.MinValue, short.MaxValue, (short)0, (short)-7],
        LatticeFieldTypeEnum.UInt16 => [ushort.MinValue, ushort.MaxValue, (ushort)7],
        LatticeFieldTypeEnum.Int32 => [int.MinValue, int.MaxValue, 0, -7],
        LatticeFieldTypeEnum.UInt32 => [uint.MinValue, uint.MaxValue, 7u],
        LatticeFieldTypeEnum.Int64 => [long.MinValue, long.MaxValue, 0L, -7L],
        LatticeFieldTypeEnum.UInt64 => [ulong.MinValue, ulong.MaxValue, 7UL],
        LatticeFieldTypeEnum.Float32 =>
        [
            0f, -0f, float.Epsilon, float.MinValue, float.MaxValue, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 3.1415927f,
        ],
        LatticeFieldTypeEnum.Float64 =>
        [
            0d, -0d, double.Epsilon, double.MinValue, double.MaxValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity, Math.PI,
        ],
        LatticeFieldTypeEnum.Decimal =>
        [
            0m, 1.00m, -1.000m, decimal.MinValue, decimal.MaxValue, 0.0000000000000000000000000001m, new decimal(0, 0, 0, isNegative: true, scale: 3),
        ],
        LatticeFieldTypeEnum.Guid => [Guid.Empty, Guid.AllBitsSet, Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0")],
        LatticeFieldTypeEnum.DateTimeOffset =>
        [
            DateTimeOffset.MinValue, DateTimeOffset.MaxValue, DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_567),
        ],
        LatticeFieldTypeEnum.DateOnly => [DateOnly.MinValue, DateOnly.MaxValue, new DateOnly(2026, 11, 17)],
        LatticeFieldTypeEnum.TimeOnly => [TimeOnly.MinValue, TimeOnly.MaxValue, new TimeOnly(12, 30, 15).Add(TimeSpan.FromTicks(7))],
        LatticeFieldTypeEnum.TimeSpan => [TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero, TimeSpan.FromTicks(-1)],
        LatticeFieldTypeEnum.String => ["", "plain", "aé€𐐨", LongText],
        LatticeFieldTypeEnum.Binary => [Array.Empty<byte>(), new byte[] { 1, 2, 3 }, BlockBytes, ChunkedBytes],

        // Level, a signed enum type of Int16 with the members -5 and 7; Mask, a Flags type of UInt32 with
        // the members 1 and 2.
        LatticeFieldTypeEnum.Enum when declaration.EnumType!.UnderlyingType == LatticeFieldTypeEnum.Int16 => [(short)-5, (short)7],
        LatticeFieldTypeEnum.Enum => [1u, 2u, 3u],
        _ => throw new ArgumentOutOfRangeException(nameof(declaration), declaration.Type, "EveryType has no field of this type."),
    };
}
