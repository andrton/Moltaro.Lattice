namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// Every scalar type an index part can be, as the model tests store it (ScalarType). The edge values are
// where an order is easy to get wrong: the ends of the type, zero, one and minus one, the sign bit and the
// high bit of an unsigned value, where the stored bytes do not sort as the value; for a floating-point type
// NaN in three forms of bits, the infinities, the two zeros and the smallest subnormals; for a Decimal its
// ends, the zeros of both signs and values of one key in other scales; for a Guid values that differ only in
// a later field; for a DateTimeOffset one instant at three offsets; for a text the empty text, case
// pairs, characters on both sides of the surrogates, and lengths at the copy of an entry (64 bytes), at the
// piece of a decoded comparison (256 code units) and in chunks (beyond 4 KiB, and beyond one chunk of
// 65,504 bytes). An Enum is given as the integer of its underlying type, from the members of its enum type
// (ScalarSchema), and Flags as a combination of declared bits.
internal static class ScalarTypes
{
    // The type of the name given, as ScalarType.Name states it.
    public static ScalarType Named(string name) => All.Single(type => type.Name == name);

    public static ScalarType Boolean { get; } = new()
    {
        Name = "Boolean",
        Declaration = "\"Type\": \"Boolean\"",
        Edges = [false, true],
        Random = random => random.Next(2) == 1,
        Run = ScalarRun.Of<bool>(),
    };

    public static ScalarType Int8 { get; } = new()
    {
        Name = "Int8",
        Declaration = "\"Type\": \"Int8\"",
        Edges = [sbyte.MinValue, (sbyte)-127, (sbyte)-64, (sbyte)-1, (sbyte)0, (sbyte)1, (sbyte)64, (sbyte)126, sbyte.MaxValue],
        Between = [(sbyte)-100, (sbyte)100],
        Least = sbyte.MinValue,
        Greatest = sbyte.MaxValue,
        Random = random => (sbyte)random.Next(sbyte.MinValue, sbyte.MaxValue + 1),
        Run = ScalarRun.Of<sbyte>(),
    };

    public static ScalarType UInt8 { get; } = new()
    {
        Name = "UInt8",
        Declaration = "\"Type\": \"UInt8\"",
        Edges = [byte.MinValue, (byte)1, (byte)0x7f, (byte)0x80, (byte)0xfe, byte.MaxValue],
        Between = [(byte)50, (byte)200],
        Greatest = byte.MaxValue,
        Random = random => (byte)random.Next(byte.MaxValue + 1),
        Run = ScalarRun.Of<byte>(),
    };

    public static ScalarType Int16 { get; } = new()
    {
        Name = "Int16",
        Declaration = "\"Type\": \"Int16\"",
        Edges = [short.MinValue, (short)(short.MinValue + 1), (short)-256, (short)-1, (short)0, (short)1, (short)255, (short)256, (short)(short.MaxValue - 1), short.MaxValue],
        Between = [(short)-12_345, (short)12_345],
        Least = short.MinValue,
        Greatest = short.MaxValue,
        Random = random => (short)random.Next(short.MinValue, short.MaxValue + 1),
        Run = ScalarRun.Of<short>(),
    };

    public static ScalarType UInt16 { get; } = new()
    {
        Name = "UInt16",
        Declaration = "\"Type\": \"UInt16\"",
        Edges = [ushort.MinValue, (ushort)1, (ushort)0xff, (ushort)0x100, (ushort)0x7fff, (ushort)0x8000, (ushort)0xfffe, ushort.MaxValue],
        Between = [(ushort)12_345, (ushort)54_321],
        Greatest = ushort.MaxValue,
        Random = random => (ushort)random.Next(ushort.MaxValue + 1),
        Run = ScalarRun.Of<ushort>(),
    };

    public static ScalarType Int32 { get; } = new()
    {
        Name = "Int32",
        Declaration = "\"Type\": \"Int32\"",
        Edges = [int.MinValue, int.MinValue + 1, -65_536, -1, 0, 1, 0x00ff_ffff, 0x0100_0000, int.MaxValue - 1, int.MaxValue],
        Between = [-123_456_789, 123_456_789],
        Least = int.MinValue,
        Greatest = int.MaxValue,
        Random = random => (int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1),
        Run = ScalarRun.Of<int>(),
    };

    public static ScalarType UInt32 { get; } = new()
    {
        Name = "UInt32",
        Declaration = "\"Type\": \"UInt32\"",
        Edges = [uint.MinValue, 1u, 0xffu, 0x7fff_ffffu, 0x8000_0000u, 0xffff_fffeu, uint.MaxValue],
        Between = [123_456_789u, 3_456_789_012u],
        Greatest = uint.MaxValue,
        Random = random => (uint)random.NextInt64((long)uint.MaxValue + 1),
        Run = ScalarRun.Of<uint>(),
    };

    public static ScalarType Int64 { get; } = new()
    {
        Name = "Int64",
        Declaration = "\"Type\": \"Int64\"",
        Edges = [long.MinValue, long.MinValue + 1, (long)int.MinValue - 1, -1L, 0L, 1L, (long)uint.MaxValue + 1, long.MaxValue - 1, long.MaxValue],
        Between = [-1_234_567_890_123L, 1_234_567_890_123L],
        Least = long.MinValue,
        Greatest = long.MaxValue,
        Random = random => random.NextInt64(long.MinValue, long.MaxValue),
        Run = ScalarRun.Of<long>(),
    };

    public static ScalarType UInt64 { get; } = new()
    {
        Name = "UInt64",
        Declaration = "\"Type\": \"UInt64\"",
        Edges = [ulong.MinValue, 1ul, (ulong)long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue - 1, ulong.MaxValue],
        Between = [1_234_567_890_123ul, 12_345_678_901_234_567_890ul],
        Greatest = ulong.MaxValue,
        Random = random => (ulong)random.NextInt64(long.MinValue, long.MaxValue),
        Run = ScalarRun.Of<ulong>(),
    };

    public static ScalarType Float32 { get; } = new()
    {
        Name = "Float32",
        Declaration = "\"Type\": \"Float32\"",
        Edges =
        [
            float.NaN, -float.NaN, BitConverter.Int32BitsToSingle(0x7fc0_0001), float.NegativeInfinity, float.MinValue, -1f,
            -float.Epsilon, -0f, 0f, float.Epsilon, 1f, 16_777_216f, float.MaxValue, float.PositiveInfinity,
        ],
        Between = [-12_345.5f, 0.25f, 12_345.5f],
        Greatest = float.PositiveInfinity,
        Random = random => random.Next(2) == 0 ? BitConverter.Int32BitsToSingle(random.Next() ^ (random.Next(2) << 31)) : (float)Math.Round((random.NextDouble() - 0.5) * 2_000, 2),
        Run = ScalarRun.Of<float>(),
    };

    public static ScalarType Float64 { get; } = new()
    {
        Name = "Float64",
        Declaration = "\"Type\": \"Float64\"",
        Edges =
        [
            double.NaN, -double.NaN, BitConverter.Int64BitsToDouble(0x7ff8_0000_0000_0001), double.NegativeInfinity, double.MinValue, -1d,
            -double.Epsilon, -0d, 0d, double.Epsilon, 1d, 9_007_199_254_740_992d, double.MaxValue, double.PositiveInfinity,
        ],
        Between = [-12_345.5, 0.25, 12_345.5],
        Greatest = double.PositiveInfinity,
        Random = random => random.Next(2) == 0 ? BitConverter.Int64BitsToDouble(random.NextInt64(long.MinValue, long.MaxValue)) : Math.Round((random.NextDouble() - 0.5) * 2_000, 2),
        Run = ScalarRun.Of<double>(),
    };

    public static ScalarType Decimal { get; } = new()
    {
        Name = "Decimal",
        Declaration = "\"Type\": \"Decimal\"",
        Edges =
        [
            decimal.MinValue, -79_228_162_514_264_337_593_543_950_334m, -1.00m, -1m, -0.5m, new decimal(0, 0, 0, isNegative: true, scale: 3),
            0m, 0.00m, 0.0000000000000000000000000001m, 1m, 1.0m, 1.00m, 7.9228162514264337593543950335m, decimal.MaxValue,
        ],
        Between = [-12_345.678m, 0.25m, 12_345.678m],
        Least = decimal.MinValue,
        Greatest = decimal.MaxValue,
        Random = random => new decimal(random.Next(), random.Next(), random.Next(4), random.Next(2) == 0, (byte)random.Next(29)),
        Run = ScalarRun.Of<decimal>(),
    };

    public static ScalarType Guid { get; } = new()
    {
        Name = "Guid",
        Declaration = "\"Type\": \"Guid\"",
        Edges =
        [
            System.Guid.Empty, System.Guid.Parse("00000000-0000-0000-0000-000000000001"), System.Guid.Parse("00000000-0000-0000-0000-000000000100"),
            System.Guid.Parse("00000000-0000-0001-0000-000000000000"), System.Guid.Parse("00000000-0001-0000-0000-000000000000"),
            System.Guid.Parse("00000001-0000-0000-0000-000000000000"), System.Guid.Parse("12345678-9abc-def0-0000-000000000000"),
            System.Guid.Parse("12345678-9abc-def0-0000-000000000001"), System.Guid.Parse("12345678-9abc-def0-8000-000000000000"),
            System.Guid.Parse("12345678-9abc-def0-ffff-ffffffffffff"), System.Guid.Parse("7fffffff-ffff-ffff-ffff-ffffffffffff"),
            System.Guid.Parse("80000000-0000-0000-0000-000000000000"), System.Guid.Parse("ffffffff-ffff-ffff-ffff-fffffffffffe"),
            System.Guid.AllBitsSet,
        ],
        Between = [System.Guid.Parse("12345678-9abc-def0-7fff-ffffffffffff"), System.Guid.Parse("3fffffff-0000-0000-0000-000000000000")],
        Greatest = System.Guid.AllBitsSet,
        Random = random =>
        {
            Span<byte> bytes = stackalloc byte[16];
            random.NextBytes(bytes);
            return new Guid(bytes);
        },
        Run = ScalarRun.Of<Guid>(),
    };

    public static ScalarType DateTimeOffset { get; } = new()
    {
        Name = "DateTimeOffset",
        Declaration = "\"Type\": \"DateTimeOffset\"",
        Edges =
        [
            System.DateTimeOffset.MinValue, System.DateTimeOffset.MinValue.AddTicks(1), System.DateTimeOffset.UnixEpoch.AddTicks(-1),
            System.DateTimeOffset.UnixEpoch, new DateTimeOffset(1970, 1, 1, 5, 30, 0, System.TimeSpan.FromMinutes(330)),
            new DateTimeOffset(1969, 12, 31, 16, 0, 0, System.TimeSpan.FromHours(-8)), new DateTimeOffset(2026, 11, 23, 12, 0, 0, System.TimeSpan.FromHours(3)),
            new DateTimeOffset(2026, 11, 23, 9, 0, 0, System.TimeSpan.Zero).AddTicks(1), System.DateTimeOffset.MaxValue.AddTicks(-1), System.DateTimeOffset.MaxValue,
        ],
        Between = [new DateTimeOffset(2000, 1, 1, 0, 0, 0, System.TimeSpan.FromHours(-5)), new DateTimeOffset(2100, 6, 15, 12, 0, 0, System.TimeSpan.Zero)],
        Greatest = System.DateTimeOffset.MaxValue,
        Random = random =>
        {
            var offset = System.TimeSpan.FromMinutes(random.Next(-14 * 60, (14 * 60) + 1));
            var utcTicks = random.NextInt64(System.TimeSpan.TicksPerDay, System.DateTimeOffset.MaxValue.UtcTicks - System.TimeSpan.TicksPerDay);
            return new DateTimeOffset(utcTicks + offset.Ticks, offset);
        },
        Run = ScalarRun.Of<DateTimeOffset>(),
    };

    public static ScalarType DateOnly { get; } = new()
    {
        Name = "DateOnly",
        Declaration = "\"Type\": \"DateOnly\"",
        Edges =
        [
            System.DateOnly.MinValue, System.DateOnly.MinValue.AddDays(1), new DateOnly(1969, 12, 31), new DateOnly(1970, 1, 1),
            new DateOnly(2000, 2, 29), new DateOnly(2026, 11, 23), System.DateOnly.MaxValue.AddDays(-1), System.DateOnly.MaxValue,
        ],
        Between = [new DateOnly(1234, 5, 6), new DateOnly(5678, 9, 10)],
        Greatest = System.DateOnly.MaxValue,
        Random = random => System.DateOnly.FromDayNumber(random.Next(System.DateOnly.MaxValue.DayNumber + 1)),
        Run = ScalarRun.Of<DateOnly>(),
    };

    public static ScalarType TimeOnly { get; } = new()
    {
        Name = "TimeOnly",
        Declaration = "\"Type\": \"TimeOnly\"",
        Edges =
        [
            System.TimeOnly.MinValue, new TimeOnly(1), new TimeOnly(new TimeOnly(12, 0).Ticks - 1), new TimeOnly(12, 0),
            new TimeOnly(23, 59, 59), System.TimeOnly.MaxValue,
        ],
        Between = [new TimeOnly(3, 33, 33, 333), new TimeOnly(18, 18, 18, 818)],
        Greatest = System.TimeOnly.MaxValue,
        Random = random => new TimeOnly(random.NextInt64(System.TimeOnly.MaxValue.Ticks + 1)),
        Run = ScalarRun.Of<TimeOnly>(),
    };

    public static ScalarType TimeSpan { get; } = new()
    {
        Name = "TimeSpan",
        Declaration = "\"Type\": \"TimeSpan\"",
        Edges =
        [
            System.TimeSpan.MinValue, System.TimeSpan.MinValue + System.TimeSpan.FromTicks(1), System.TimeSpan.FromDays(-1), System.TimeSpan.FromTicks(-1),
            System.TimeSpan.Zero, System.TimeSpan.FromTicks(1), System.TimeSpan.FromDays(1), System.TimeSpan.MaxValue - System.TimeSpan.FromTicks(1),
            System.TimeSpan.MaxValue,
        ],
        Between = [System.TimeSpan.FromDays(-12_345.5), System.TimeSpan.FromDays(12_345.5)],
        Least = System.TimeSpan.MinValue,
        Greatest = System.TimeSpan.MaxValue,
        Random = random => System.TimeSpan.FromTicks(random.NextInt64(long.MinValue, long.MaxValue)),
        Run = ScalarRun.Of<TimeSpan>(),
    };

    // Grade over Int16 (ScalarSchema); Unused, 7, is never stored.
    public static ScalarType EnumOverInt16 { get; } = new()
    {
        Name = "Enum over Int16",
        Declaration = "\"Type\": \"Enum\", \"Enum\": \"Grade\"",
        Edges = GradeMembers,
        Between = [(short)7],
        Least = short.MinValue,
        Greatest = short.MaxValue,
        Random = random => GradeMembers[random.Next(GradeMembers.Length)],
        Run = ScalarRun.Of<short>(),
    };

    // Span over Int64 (ScalarSchema); Unused, 7, is never stored.
    public static ScalarType EnumOverInt64 { get; } = new()
    {
        Name = "Enum over Int64",
        Declaration = "\"Type\": \"Enum\", \"Enum\": \"Span\"",
        Edges = SpanMembers,
        Between = [7L],
        Least = long.MinValue,
        Greatest = long.MaxValue,
        Random = random => SpanMembers[random.Next(SpanMembers.Length)],
        Run = ScalarRun.Of<long>(),
    };

    // Wide over UInt64 (ScalarSchema); Unused, 7, is never stored.
    public static ScalarType EnumOverUInt64 { get; } = new()
    {
        Name = "Enum over UInt64",
        Declaration = "\"Type\": \"Enum\", \"Enum\": \"Wide\"",
        Edges = WideMembers,
        Between = [7ul],
        Greatest = ulong.MaxValue,
        Random = random => WideMembers[random.Next(WideMembers.Length)],
        Run = ScalarRun.Of<ulong>(),
    };

    // Rights over Int32 (ScalarSchema): any combination of Read 1, Write 2, Delete 4, Share 2^30 and the sign
    // bit; Unused, 8, is never stored, alone or with another bit.
    public static ScalarType Flags { get; } = new()
    {
        Name = "Flags",
        Declaration = "\"Type\": \"Enum\", \"Enum\": \"Rights\"",
        Edges = [int.MinValue, int.MinValue | 7, int.MinValue | 1, int.MinValue | 0x4000_0007, 0, 1, 2, 4, 7, 0x4000_0000, 0x4000_0007],
        Between = [8, 9],
        Least = int.MinValue,
        Greatest = 0x4000_0007,
        Random = random => RightsBits.Where(_ => random.Next(2) == 0).Aggregate(0, (rights, bit) => rights | bit),
        Run = ScalarRun.Of<int>(),
    };

    public static ScalarType TextOrdinal { get; } = new()
    {
        Name = "String Ordinal",
        Declaration = "\"Type\": \"String\"",
        Order = IndexModel.Ordinal,
        Edges = TextEdges,
        Between = TextBetween,
        Random = RandomText,
        Run = ScalarRun.OfText(),
    };

    public static ScalarType TextWithoutCase { get; } = new()
    {
        Name = "String without case",
        Declaration = "\"Type\": \"String\", \"Comparison\": \"OrdinalIgnoreCase\"",
        Order = IndexModel.WithoutCase,
        Edges = TextEdges,
        Between = TextBetween,
        Random = RandomText,
        Run = ScalarRun.OfText(),
    };

    private static ScalarType[] All =>
    [
        Boolean, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64, Decimal, Guid, DateTimeOffset, DateOnly,
        TimeOnly, TimeSpan, EnumOverInt16, EnumOverInt64, EnumOverUInt64, Flags, TextOrdinal, TextWithoutCase,
    ];

    private static object[] GradeMembers => [short.MinValue, (short)-300, (short)-2, (short)-1, (short)0, (short)1, (short)5, (short)255, (short)256, (short)300, short.MaxValue];

    private static object[] SpanMembers => [long.MinValue, -4_294_967_296L, -2L, -1L, 0L, 1L, 4_294_967_296L, long.MaxValue];

    private static object[] WideMembers => [0ul, 1ul, 9_223_372_036_854_775_807ul, 9_223_372_036_854_775_808ul, 9_223_372_036_854_775_813ul, 18_446_744_073_709_551_614ul, 18_446_744_073_709_551_615ul];

    private static int[] RightsBits => [1, 2, 4, 0x4000_0000, int.MinValue];

    // The characters of random texts: letters in both cases, letters whose case mapping leaves ASCII or the
    // basic plane, a character on each side of the surrogates and two beyond U+FFFF. No m, M or x, so that
    // the texts never stored (TextBetween) are not drawn.
    private static readonly string[] Alphabet = ["a", "A", "b", "B", "z", "Z", "s", "S", "ſ", "i", "I", "ı", "İ", "é", "É", "ß", "ẞ", "～", "\uE000", "\uFFFD", "\U0001F600", "\U00010428", "\U00010400", "0", " "];

    private static object[] TextBetween => ["m", "Mm", "xx", "\uFFFF\uFFFF"];

    private static object[] TextEdges =>
    [
        "", "a", "A", "b", "ab", "aB", "Ab", "é", "É", "ß", "ẞ", "ı", "I", "i", "İ", "ſ", "s", "S", "ǅ", "ǆ", "Ǆ", "～", "\uE000", "\uFFFD",
        "\U00010000", "\U00010400", "\U00010428", "\U0001F600", "\U0010FFFF",
        Long("x", 63, "a"), Long("x", 63, "b"), Long("X", 63, "A"), Long("x", 64, "a"), Long("x", 64, "b"),
        Long("é", 32, ""), Long("é", 33, ""), Long("\U0001F600", 16, ""), Long("\U0001F600", 17, ""),
        Long("x", 255, "a"), Long("x", 256, "a"), Long("x", 256, "b"), Long("X", 256, "a"), Long("x", 256, "\uE000"), Long("x", 256, "\U0001F600"),
        Long("é", 128, ""), Long("é", 129, ""),
        Long("x", 4_200, "a"), Long("x", 4_200, "b"), Long("X", 4_200, "a"),
        Long("x", 70_000, "a"), Long("x", 70_000, "b"),
    ];

    private static string RandomText(Random random)
    {
        var length = random.Next(13);
        var text = new System.Text.StringBuilder();

        for (var place = 0; place < length; place++)
        {
            text.Append(Alphabet[random.Next(Alphabet.Length)]);
        }

        return text.ToString();
    }

    // A text of the unit repeated, then the tail.
    private static string Long(string unit, int count, string tail) => string.Concat(Enumerable.Repeat(unit, count)) + tail;
}
