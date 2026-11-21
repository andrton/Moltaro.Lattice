namespace Moltaro.Lattice.Tests.Indexes.Declaration;

// Every scalar type an index part can have, and an Enum, is found by the full key of an index on it
// (decision on index access; docs/design/ordered-index.md, "Entries and their order": keys are compared
// by the comparison of their field, never as raw bytes). The entity type Sample has a nullable field of
// each type and an index on each. For each type four entities are written by name: two whose values are
// one key, one with another value, one with none; a search through a class and by name returns the
// first two for the key, the third for its value and the fourth for null, and nothing for a value no
// entity has.
// - Two values that are one key in other bytes are one key: 0 and -0 of a Float32 or a Float64, which
//   CompareTo holds equal; 1.0 and 1.00 of a Decimal. Each entity returns the value it was written with,
//   in its own bytes. A NaN is a value like another and is found by a NaN.
// - The values are the ends of the range of their type, where a value that is cut to another width or
//   read with the sign of another type would be another key.
// - An Enum part is given as its underlying integer.
// Text keys are stated by TextKeySearchTests.
public sealed class IndexPartValueTests
{
    public static IEnumerable<TheoryDataRow<string>> Types =>
    [
        new("Boolean"), new("Int8"), new("UInt8"), new("Int16"), new("UInt16"), new("Int32"), new("UInt32"),
        new("Int64"), new("UInt64"), new("Float32"), new("Float64"), new("Decimal"), new("Guid"),
        new("DateTimeOffset"), new("DateOnly"), new("TimeOnly"), new("TimeSpan"), new("Level"),
    ];

    [Theory]
    [MemberData(nameof(Types))]
    public async Task Part_of_each_scalar_type_and_an_Enum_is_found_by_its_value_and_by_null_through_a_class_and_by_name(string type)
    {
        await using var database = await TestData.OpenMasterAsync(schema: Schema);
        var records = database.Set("Sample");
        var (key, sameKey, other, absent) = ValuesOf(type);

        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, [type] = key });
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 2L, [type] = sameKey });
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 3L, [type] = other });
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 4L });

        var byName = records.Index($"By{type}");
        var byClass = database.Set<Sample>().Index($"By{type}");

        Assert.Equal([1L, 2L], IdsOf(byName.Find(IndexKey.Equal<object?>(key))));
        Assert.Equal([1L, 2L], IdsOf(byName.Find(IndexKey.Equal<object?>(sameKey))));
        Assert.Equal([1L, 2L], IdsOf(byClass.Find(IndexKey.Equal<object?>(key))));
        Assert.Equal([3L], IdsOf(byName.Find(IndexKey.Equal<object?>(other))));
        Assert.Equal([3L], IdsOf(byClass.Find(IndexKey.Equal<object?>(other))));
        Assert.Equal([4L], IdsOf(byName.Find(IndexKey.Equal<object?>(null))));
        Assert.Equal([4L], IdsOf(byClass.Find(IndexKey.Equal<object?>(null))));
        // A Boolean has no value besides the two the entities hold.
        if (absent is not null)
        {
            Assert.Empty(byName.Find(IndexKey.Equal<object?>(absent)));
            Assert.Empty(byClass.Find(IndexKey.Equal<object?>(absent)));
        }

        // Each entity of the key returns its own value, in the bytes it was written with.
        foreach (var found in byName.Find(IndexKey.Equal<object?>(key)))
        {
            var written = found.Get<long>("Id") == 1 ? key : sameKey;

            Assert.Equal(BitsOf(written), BitsOf(found[type]));
        }
    }

    // Two values of the type that are one key, the second in other bytes where the type has such, another
    // value, and a value no entity has, null for a type that has none left; as a record of the type
    // holds them.
    private static (object Key, object SameKey, object Other, object? Absent) ValuesOf(string type) => type switch
    {
        "Boolean" => (true, true, false, null),
        "Int8" => (sbyte.MinValue, sbyte.MinValue, sbyte.MaxValue, (sbyte)0),
        "UInt8" => (byte.MaxValue, byte.MaxValue, byte.MinValue, (byte)1),
        "Int16" => (short.MinValue, short.MinValue, short.MaxValue, (short)-1),
        "UInt16" => (ushort.MaxValue, ushort.MaxValue, ushort.MinValue, (ushort)0x8000),
        "Int32" => (int.MinValue, int.MinValue, int.MaxValue, -1),
        "UInt32" => (uint.MaxValue, uint.MaxValue, uint.MinValue, 0x8000_0000u),
        "Int64" => (long.MinValue, long.MinValue, long.MaxValue, -1L),
        "UInt64" => (ulong.MaxValue, ulong.MaxValue, ulong.MinValue, 0x8000_0000_0000_0000ul),
        "Float32" => (0f, -0f, float.NaN, float.Epsilon),
        "Float64" => (-0d, 0d, double.NaN, double.NegativeInfinity),
        "Decimal" => (1.0m, 1.00m, decimal.MinValue, 1.000_000_000_000_000_000_000_000_1m),
        "Guid" => (Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), Guid.Empty),
        "DateTimeOffset" => (DateTimeOffset.MinValue, DateTimeOffset.MinValue, new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero), DateTimeOffset.MaxValue),
        "DateOnly" => (DateOnly.MaxValue, DateOnly.MaxValue, DateOnly.MinValue, new DateOnly(2026, 11, 19)),
        "TimeOnly" => (TimeOnly.MaxValue, TimeOnly.MaxValue, TimeOnly.MinValue, new TimeOnly(12, 0)),
        "TimeSpan" => (TimeSpan.MinValue, TimeSpan.MinValue, TimeSpan.MaxValue, TimeSpan.Zero),
        _ => (1, 1, 0, 2),
    };

    // A value with the bytes that tell values of one key apart: the scale of a Decimal and the sign of a
    // zero.
    private static object? BitsOf(object? value) => value switch
    {
        decimal number => decimal.GetBits(number).Aggregate(string.Empty, (bits, part) => bits + part.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)),
        double number => BitConverter.DoubleToInt64Bits(number),
        float number => BitConverter.SingleToInt32Bits(number),
        _ => value,
    };

    private static long[] IdsOf(IEnumerable<LatticeRecord> found) => [.. found.Select(record => record.Get<long>("Id")).Order()];

    private static long[] IdsOf(IEnumerable<Sample> found) => [.. found.Select(sample => sample.Id).Order()];

    private static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "Enums": [ { "Id": 1, "Name": "Level", "Members": [ { "Name": "Low", "Value": 0 }, { "Name": "High", "Value": 1 }, { "Name": "Top", "Value": 2 } ] } ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Sample", "ClrType": "{{typeof(Sample).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Boolean", "Type": "Boolean", "Nullable": true },
                { "Id": 3, "Name": "Int8", "Type": "Int8", "Nullable": true },
                { "Id": 4, "Name": "UInt8", "Type": "UInt8", "Nullable": true },
                { "Id": 5, "Name": "Int16", "Type": "Int16", "Nullable": true },
                { "Id": 6, "Name": "UInt16", "Type": "UInt16", "Nullable": true },
                { "Id": 7, "Name": "Int32", "Type": "Int32", "Nullable": true },
                { "Id": 8, "Name": "UInt32", "Type": "UInt32", "Nullable": true },
                { "Id": 9, "Name": "Int64", "Type": "Int64", "Nullable": true },
                { "Id": 10, "Name": "UInt64", "Type": "UInt64", "Nullable": true },
                { "Id": 11, "Name": "Float32", "Type": "Float32", "Nullable": true },
                { "Id": 12, "Name": "Float64", "Type": "Float64", "Nullable": true },
                { "Id": 13, "Name": "Decimal", "Type": "Decimal", "Nullable": true },
                { "Id": 14, "Name": "Guid", "Type": "Guid", "Nullable": true },
                { "Id": 15, "Name": "DateTimeOffset", "Type": "DateTimeOffset", "Nullable": true },
                { "Id": 16, "Name": "DateOnly", "Type": "DateOnly", "Nullable": true },
                { "Id": 17, "Name": "TimeOnly", "Type": "TimeOnly", "Nullable": true },
                { "Id": 18, "Name": "TimeSpan", "Type": "TimeSpan", "Nullable": true },
                { "Id": 19, "Name": "Level", "Type": "Enum", "Enum": "Level", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 2, "Name": "ByBoolean", "Fields": [2] },
                { "Id": 3, "Name": "ByInt8", "Fields": [3] },
                { "Id": 4, "Name": "ByUInt8", "Fields": [4] },
                { "Id": 5, "Name": "ByInt16", "Fields": [5] },
                { "Id": 6, "Name": "ByUInt16", "Fields": [6] },
                { "Id": 7, "Name": "ByInt32", "Fields": [7] },
                { "Id": 8, "Name": "ByUInt32", "Fields": [8] },
                { "Id": 9, "Name": "ByInt64", "Fields": [9] },
                { "Id": 10, "Name": "ByUInt64", "Fields": [10] },
                { "Id": 11, "Name": "ByFloat32", "Fields": [11] },
                { "Id": 12, "Name": "ByFloat64", "Fields": [12] },
                { "Id": 13, "Name": "ByDecimal", "Fields": [13] },
                { "Id": 14, "Name": "ByGuid", "Fields": [14] },
                { "Id": 15, "Name": "ByDateTimeOffset", "Fields": [15] },
                { "Id": 16, "Name": "ByDateOnly", "Fields": [16] },
                { "Id": 17, "Name": "ByTimeOnly", "Fields": [17] },
                { "Id": 18, "Name": "ByTimeSpan", "Fields": [18] },
                { "Id": 19, "Name": "ByLevel", "Fields": [19] }
              ]
            }
          ]
        }
        """);

    private sealed class Sample
    {
        public long Id { get; set; }

        public bool? Boolean { get; set; }

        public sbyte? Int8 { get; set; }

        public byte? UInt8 { get; set; }

        public short? Int16 { get; set; }

        public ushort? UInt16 { get; set; }

        public int? Int32 { get; set; }

        public uint? UInt32 { get; set; }

        public long? Int64 { get; set; }

        public ulong? UInt64 { get; set; }

        public float? Float32 { get; set; }

        public double? Float64 { get; set; }

        public decimal? Decimal { get; set; }

        public Guid? Guid { get; set; }

        public DateTimeOffset? DateTimeOffset { get; set; }

        public DateOnly? DateOnly { get; set; }

        public TimeOnly? TimeOnly { get; set; }

        public TimeSpan? TimeSpan { get; set; }

        public int? Level { get; set; }
    }
}
