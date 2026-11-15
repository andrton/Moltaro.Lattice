using System.Globalization;

namespace Moltaro.Lattice.Tests.EnumFields;

// The engine stores an entity type with Enum fields from Epic 1, sprint S07 (brief of the sprint,
// "Types and members"): ordinary and Flags, nullable or not, at each of the eight underlying integer
// types, an entity is written and read back equal, through its class, whose properties are .NET enums
// that agree with the enum types, and by name, where a record holds the underlying integer. The
// values are the smallest and the largest a type has, null, zero as a Flags value that no member
// names, and combinations of bits that no member names with the highest bit of the type, the sign bit
// of a signed one: a value that is cut to another width, extended with its sign, or read as another
// type on its way in or out is read back as another value, or is refused as one its enum type does not
// declare.
//
// Engine only: the placeholder refuses an entity type with an Enum field until S12. Whether a value
// is declared is checked on both implementations, as EnumValidationTests states.
public sealed class EnumFieldTests
{
    private enum Int8LevelEnum : sbyte
    {
        Lowest = sbyte.MinValue,
        One = 1,
        Highest = sbyte.MaxValue,
    }

    [Flags]
    private enum Int8MaskEnum : sbyte
    {
        First = 1,
        Second = 2,
        Top = sbyte.MinValue,
    }

    private enum UInt8LevelEnum : byte
    {
        Lowest = byte.MinValue,
        One = 1,
        Highest = byte.MaxValue,
    }

    [Flags]
    private enum UInt8MaskEnum : byte
    {
        First = 1,
        Second = 2,
        Top = 0x80,
    }

    private enum Int16LevelEnum : short
    {
        Lowest = short.MinValue,
        One = 1,
        Highest = short.MaxValue,
    }

    [Flags]
    private enum Int16MaskEnum : short
    {
        First = 1,
        Second = 2,
        Top = short.MinValue,
    }

    private enum UInt16LevelEnum : ushort
    {
        Lowest = ushort.MinValue,
        One = 1,
        Highest = ushort.MaxValue,
    }

    [Flags]
    private enum UInt16MaskEnum : ushort
    {
        First = 1,
        Second = 2,
        Top = 0x8000,
    }

    private enum Int32LevelEnum
    {
        Lowest = int.MinValue,
        One = 1,
        Highest = int.MaxValue,
    }

    [Flags]
    private enum Int32MaskEnum
    {
        First = 1,
        Second = 2,
        Top = int.MinValue,
    }

    private enum UInt32LevelEnum : uint
    {
        Lowest = uint.MinValue,
        One = 1,
        Highest = uint.MaxValue,
    }

    [Flags]
    private enum UInt32MaskEnum : uint
    {
        First = 1,
        Second = 2,
        Top = 0x8000_0000,
    }

    private enum Int64LevelEnum : long
    {
        Lowest = long.MinValue,
        One = 1,
        Highest = long.MaxValue,
    }

    [Flags]
    private enum Int64MaskEnum : long
    {
        First = 1,
        Second = 2,
        Top = long.MinValue,
    }

    private enum UInt64LevelEnum : ulong
    {
        Lowest = ulong.MinValue,
        One = 1,
        Highest = ulong.MaxValue,
    }

    [Flags]
    private enum UInt64MaskEnum : ulong
    {
        First = 1,
        Second = 2,
        Top = 0x8000_0000_0000_0000,
    }

    private static readonly Dictionary<string, WidthCase> Cases = new WidthCase[]
    {
        new WidthCase<Int8LevelEnum, Int8MaskEnum>("Int8"),
        new WidthCase<UInt8LevelEnum, UInt8MaskEnum>("UInt8"),
        new WidthCase<Int16LevelEnum, Int16MaskEnum>("Int16"),
        new WidthCase<UInt16LevelEnum, UInt16MaskEnum>("UInt16"),
        new WidthCase<Int32LevelEnum, Int32MaskEnum>("Int32"),
        new WidthCase<UInt32LevelEnum, UInt32MaskEnum>("UInt32"),
        new WidthCase<Int64LevelEnum, Int64MaskEnum>("Int64"),
        new WidthCase<UInt64LevelEnum, UInt64MaskEnum>("UInt64"),
    }.ToDictionary(@case => @case.UnderlyingType);

    // The entity type Note of the moved test: an Enum field held as its integer, beside the text.
    private static readonly LatticeSchema NoteSchema = LatticeSchema.Compile($$"""
        {
          "Enums": [ { "Id": 1, "Name": "Level", "UnderlyingType": "Int32", "Members": [ { "Name": "Low", "Value": 1 } ] } ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(Note).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Caption", "Type": "String" }, { "Id": 3, "Name": "Level", "Type": "Enum", "Enum": "Level" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static TheoryData<string> UnderlyingTypes => [.. Cases.Keys];

    [Theory]
    [MemberData(nameof(UnderlyingTypes))]
    public async Task Enum_fields_ordinary_and_Flags_nullable_or_not_are_written_and_read_back_equal(string underlyingType)
    {
        await Cases[underlyingType].ExpectWrittenAndReadBackAsync();
    }

    // Moved here from MalformedTextWriteTests, whose case on the engine stated that such text is refused
    // before the write is refused as not implemented: the engine stores the entity type from this
    // sprint, so a write of well-formed text is stored.
    [Fact]
    public async Task Text_that_is_not_well_formed_in_an_entity_type_with_an_Enum_field_is_ValidationFailed_and_well_formed_text_is_stored()
    {
        await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, NoteSchema);
        var notes = database.Set<Note>();
        var records = database.Set("Note");
        var record = new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Caption"] = "a\uDE00b", ["Level"] = 1 };

        await TestData.ExpectValidationFailedAsync("Caption", () => records.InsertAsync(record));
        await TestData.ExpectValidationFailedAsync("Caption", () => records.UpsertAsync(record));
        await TestData.ExpectValidationFailedAsync("Caption", () => notes.InsertAsync(new Note { Id = 1, Caption = "a\uDE00b", Level = 1 }));
        TestData.ExpectValidationFailed("Caption", () => database.CreateWriteBatch().Insert(record));
        Assert.Empty(notes.Enumerate());

        record["Caption"] = "well-formed";
        await records.InsertAsync(record);
        await notes.InsertAsync(new Note { Id = 2, Caption = "well-formed too", Level = 1 });

        Assert.Equal("well-formed", notes.Get(1L)!.Caption);
        Assert.Equal("well-formed too", records.Get(2L)!["Caption"]);
    }

    // The bits of an enum value as an integer: its underlying value, with its sign.
    private static Int128 BitsOf<TEnum>(TEnum value)
        where TEnum : struct, Enum => UnderlyingOf(value) switch
        {
            sbyte number => number,
            byte number => number,
            short number => number,
            ushort number => number,
            int number => number,
            uint number => number,
            long number => number,
            ulong number => number,
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };

    // The enum value of the given bits, which fit its underlying type.
    private static TEnum EnumOf<TEnum>(Int128 bits)
        where TEnum : struct, Enum
    {
        var underlying = Enum.GetUnderlyingType(typeof(TEnum));
        object value = Type.GetTypeCode(underlying) switch
        {
            TypeCode.SByte => (sbyte)bits,
            TypeCode.Byte => (byte)bits,
            TypeCode.Int16 => (short)bits,
            TypeCode.UInt16 => (ushort)bits,
            TypeCode.Int32 => (int)bits,
            TypeCode.UInt32 => (uint)bits,
            TypeCode.Int64 => (long)bits,
            _ => (ulong)bits,
        };

        return (TEnum)Enum.ToObject(typeof(TEnum), value);
    }

    // The value as a record holds it: the integer of the underlying type.
    private static object UnderlyingOf<TEnum>(TEnum value)
        where TEnum : struct, Enum =>
        Convert.ChangeType(value, Enum.GetUnderlyingType(typeof(TEnum)), CultureInfo.InvariantCulture);

    // One underlying type: the enum types Level and Mask of the schema at that type, from the .NET enums
    // they agree with, and Gauge, the entity type with their four fields.
    private abstract class WidthCase(string underlyingType)
    {
        public string UnderlyingType { get; } = underlyingType;

        public abstract Task ExpectWrittenAndReadBackAsync();
    }

    private sealed class WidthCase<TLevel, TMask>(string underlyingType) : WidthCase(underlyingType)
        where TLevel : struct, Enum
        where TMask : struct, Enum
    {
        private readonly LatticeSchema _schema = LatticeSchema.Compile($$"""
            {
              "Enums": [
                { "Id": 1, "Name": "Level", "UnderlyingType": "{{underlyingType}}", "Members": [ {{MembersOf<TLevel>()}} ] },
                { "Id": 2, "Name": "Mask", "UnderlyingType": "{{underlyingType}}", "Flags": true, "Members": [ {{MembersOf<TMask>()}} ] }
              ],
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Gauge", "ClrType": "{{typeof(Gauge<TLevel, TMask>).FullName}}",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Level", "Type": "Enum", "Enum": "Level" },
                    { "Id": 3, "Name": "Spare", "Type": "Enum", "Enum": "Level", "Nullable": true },
                    { "Id": 4, "Name": "Mask", "Type": "Enum", "Enum": "Mask" },
                    { "Id": 5, "Name": "Extra", "Type": "Enum", "Enum": "Mask", "Nullable": true }
                  ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);

        public override async Task ExpectWrittenAndReadBackAsync()
        {
            await using var database = await TestDatabases.OpenMasterAsync(DatabaseImplementationEnum.Engine, _schema);
            var gauges = database.Set<Gauge<TLevel, TMask>>();
            var records = database.Set("Gauge");
            var lowest = Enum.Parse<TLevel>("Lowest");
            var one = Enum.Parse<TLevel>("One");
            var highest = Enum.Parse<TLevel>("Highest");
            var first = Enum.Parse<TMask>("First");
            var second = Enum.Parse<TMask>("Second");
            var top = Enum.Parse<TMask>("Top");

            // Zero, which no member of Mask names.
            TMask none = default;

            // The first entity through the class, the second by name.
            Gauge<TLevel, TMask>[] written =
            [
                new() { Id = 1, Level = lowest, Spare = null, Mask = none, Extra = Or(first, top) },
                new() { Id = 2, Level = highest, Spare = one, Mask = Or(Or(first, second), top), Extra = null },
            ];

            await gauges.InsertAsync(written[0]);
            await records.InsertAsync(RecordOf(records, written[1]));

            ExpectReadBack(gauges, records, written);

            // Replaced, the first by name and the second through the class.
            Gauge<TLevel, TMask>[] replaced =
            [
                new() { Id = 1, Level = one, Spare = highest, Mask = top, Extra = none },
                new() { Id = 2, Level = lowest, Spare = lowest, Mask = second, Extra = Or(second, top) },
            ];

            await records.ReplaceAsync(RecordOf(records, replaced[0]));
            await gauges.ReplaceAsync(replaced[1]);

            ExpectReadBack(gauges, records, replaced);
        }

        // Every gauge reads back with the values written, through the class and by name.
        private static void ExpectReadBack(ILatticeSet<Gauge<TLevel, TMask>> gauges, ILatticeSet records, Gauge<TLevel, TMask>[] written)
        {
            foreach (var expected in written)
            {
                var gauge = gauges.Get(expected.Id);
                var record = records.Get(expected.Id);

                Assert.NotNull(gauge);
                Assert.Equal(expected.Level, gauge.Level);
                Assert.Equal(expected.Spare, gauge.Spare);
                Assert.Equal(expected.Mask, gauge.Mask);
                Assert.Equal(expected.Extra, gauge.Extra);

                Assert.NotNull(record);
                Assert.Equal(UnderlyingOf(expected.Level), record["Level"]);
                Assert.Equal(expected.Spare is { } spare ? UnderlyingOf(spare) : null, record["Spare"]);
                Assert.Equal(UnderlyingOf(expected.Mask), record["Mask"]);
                Assert.Equal(expected.Extra is { } extra ? UnderlyingOf(extra) : null, record["Extra"]);
            }

            Assert.Equal(written.Length, gauges.Enumerate().Count());
        }

        private static LatticeRecord RecordOf(ILatticeSet records, Gauge<TLevel, TMask> gauge) => new(records.EntityType)
        {
            ["Id"] = gauge.Id,
            ["Level"] = UnderlyingOf(gauge.Level),
            ["Spare"] = gauge.Spare is { } spare ? UnderlyingOf(spare) : null,
            ["Mask"] = UnderlyingOf(gauge.Mask),
            ["Extra"] = gauge.Extra is { } extra ? UnderlyingOf(extra) : null,
        };

        private static TMask Or(TMask left, TMask right) => EnumOf<TMask>(BitsOf(left) | BitsOf(right));

        // The members of the .NET enum as the members of an enum type of the descriptor.
        private static string MembersOf<TEnum>()
            where TEnum : struct, Enum =>
            string.Join(", ", Enum.GetValues<TEnum>().Select(value => $$"""{ "Name": "{{value}}", "Value": {{BitsOf(value).ToString(CultureInfo.InvariantCulture)}} }"""));
    }

    // The class of Gauge at one underlying type.
    private sealed class Gauge<TLevel, TMask>
        where TLevel : struct, Enum
        where TMask : struct, Enum
    {
        public long Id { get; set; }

        public TLevel Level { get; set; }

        public TLevel? Spare { get; set; }

        public TMask Mask { get; set; }

        public TMask? Extra { get; set; }
    }

    private sealed class Note
    {
        public long Id { get; set; }

        public string Caption { get; set; } = "";

        public int Level { get; set; }
    }
}
