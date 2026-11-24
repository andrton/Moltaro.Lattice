using System.Globalization;

namespace Moltaro.Lattice.Tests.Records;

// A value written by name, the value of a field of a record or of an owned value, is accepted by the allowlist
// of a part of a search key (docs/decisions/queries.md, Q07.3.6, "Numeric query inputs"; the remarks of
// IndexKey) and held as the .NET type of the field: a value of that type; an integer of another width that
// fits; any integer for a Decimal; an integer that a Float32 or a Float64 holds exactly; a Float32 for a
// Float64; a Float64 for a Float32 when it converts back to itself, with NaN, an infinity and the sign of zero
// kept. Everything else is refused with the ArgumentException of a value that cannot be used for the field,
// in the words that name the field, its type and the value (DynamicSetTests): a Decimal for a floating-point
// field and the reverse, a floating-point value or a Decimal for an integer field, 42.0 included, an integer
// a Float32 or a Float64 does not hold exactly, a Float64 that does not convert back. The elements of a
// collection are taken only as declared, and an Enum field refuses at the write an integer its enum type does
// not declare, as it does today.
//
// The schema: Priced, whose key Code is a String, holds Price, a Decimal, Ratio, a Float32, Weight, a Float64,
// Count, an Int32, Total, an Int64, Level, an Enum of the integers 0 and 1 over UInt8, Prices, a List of
// Decimal, and Detail, an owned value of the fields Price to Level again.
public sealed class ValuesByNameTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "Enums": [ { "Id": 1, "Name": "Level", "UnderlyingType": "UInt8", "Members": [ { "Name": "Low", "Value": 0 }, { "Name": "High", "Value": 1 } ] } ],
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Detail",
              "Fields": [
                { "Id": 1, "Name": "Price", "Type": "Decimal", "Nullable": true },
                { "Id": 2, "Name": "Ratio", "Type": "Float32", "Nullable": true },
                { "Id": 3, "Name": "Weight", "Type": "Float64", "Nullable": true },
                { "Id": 4, "Name": "Count", "Type": "Int32", "Nullable": true },
                { "Id": 5, "Name": "Total", "Type": "Int64", "Nullable": true },
                { "Id": 6, "Name": "Level", "Type": "Enum", "Enum": "Level", "Nullable": true }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Priced",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Price", "Type": "Decimal", "Nullable": true },
                { "Id": 3, "Name": "Ratio", "Type": "Float32", "Nullable": true },
                { "Id": 4, "Name": "Weight", "Type": "Float64", "Nullable": true },
                { "Id": 5, "Name": "Count", "Type": "Int32", "Nullable": true },
                { "Id": 6, "Name": "Total", "Type": "Int64", "Nullable": true },
                { "Id": 7, "Name": "Level", "Type": "Enum", "Enum": "Level", "Nullable": true },
                { "Id": 8, "Name": "Prices", "Type": "List", "Element": { "Type": "Decimal" }, "Nullable": true },
                { "Id": 9, "Name": "Detail", "Type": "Owned", "OwnedType": "Detail", "Nullable": true }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private static readonly Dictionary<string, (string Field, object Given, object Held)> Accepted = new (string Field, object Given, object Held)[]
    {
        // A value of the type of the field, and an integer of another width that fits.
        ("Price", 2.5m, 2.5m),
        ("Count", (short)-5, -5),
        ("Count", 7L, 7),
        ("Total", uint.MaxValue, 4_294_967_295L),

        // Any integer for a Decimal.
        ("Price", 5, 5m),
        ("Price", (sbyte)-128, -128m),
        ("Price", long.MinValue, -9_223_372_036_854_775_808m),
        ("Price", ulong.MaxValue, 18_446_744_073_709_551_615m),

        // An integer a Float32 or a Float64 holds exactly: 2^24 and 2^53, and their negatives.
        ("Ratio", 16_777_216, 16_777_216f),
        ("Ratio", -16_777_216L, -16_777_216f),
        ("Weight", 9_007_199_254_740_992L, 9_007_199_254_740_992d),
        ("Weight", (ulong)long.MaxValue + 1, 9_223_372_036_854_775_808d),

        // A Float32 for a Float64, NaN, the infinities and the sign of zero kept.
        ("Weight", 0.1f, (double)0.1f),
        ("Weight", float.NaN, double.NaN),
        ("Weight", float.NegativeInfinity, double.NegativeInfinity),
        ("Weight", -0.0f, -0.0d),

        // A Float64 for a Float32 that converts back to itself, NaN, the infinities and the sign of zero kept.
        ("Ratio", 0.5d, 0.5f),
        ("Ratio", (double)float.MaxValue, float.MaxValue),
        ("Ratio", double.NaN, float.NaN),
        ("Ratio", double.PositiveInfinity, float.PositiveInfinity),
        ("Ratio", -0.0d, -0.0f),
    }.ToDictionary(row => $"{row.Field} from {row.Given.GetType().Name} {Show(row.Given)}");

    private static readonly Dictionary<string, (string Field, object Given)> Refused = new (string Field, object Given)[]
    {
        // A Decimal for a floating-point field, and the reverse.
        ("Weight", 1m),
        ("Ratio", 1m),
        ("Price", 1.0d),
        ("Price", 1.0f),

        // A floating-point value or a Decimal for an integer field, also when it is whole.
        ("Count", 42.0d),
        ("Count", 42m),
        ("Total", 42.0f),

        // An integer a Float32 or a Float64 does not hold exactly.
        ("Ratio", 16_777_217),
        ("Weight", 9_007_199_254_740_993L),

        // A Float64 that does not convert back to itself: a fraction a Float32 does not hold, an overflow and a
        // subnormal, which are not taken as an infinity or a zero.
        ("Ratio", 0.1d),
        ("Ratio", 1e300),
        ("Ratio", double.Epsilon),

        // An integer that does not fit its field, as before.
        ("Count", long.MaxValue),
    }.ToDictionary(row => $"{row.Field} from {row.Given.GetType().Name} {Show(row.Given)}");

    public static TheoryData<string> AcceptedRows => [.. Accepted.Keys];

    public static TheoryData<string> RefusedRows => [.. Refused.Keys];

    [Theory]
    [MemberData(nameof(AcceptedRows))]
    public async Task Value_written_by_name_is_accepted_by_the_allowlist_of_a_search_key_and_held_as_the_type_of_its_field(string row)
    {
        var (field, given, held) = Accepted[row];

        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var priced = database.Set("Priced");
        var record = new LatticeRecord(priced.EntityType) { ["Code"] = "written" };
        var detail = new LatticeOwnedValue(Detail()) { [field] = given };

        record[field] = given;
        record["Detail"] = detail;

        ExpectHeld(held, record[field]);
        ExpectHeld(held, detail[field]);
        ExpectHeld(held, record.Get<LatticeOwnedValue>("Detail")![field]);

        // Written, the value is read back as it is held.
        await priced.InsertAsync(record);

        var read = priced.Get("written")!;
        ExpectHeld(held, read[field]);
        ExpectHeld(held, read.Get<LatticeOwnedValue>("Detail")![field]);
    }

    [Theory]
    [MemberData(nameof(RefusedRows))]
    public void Value_the_allowlist_refuses_is_refused_in_the_words_that_name_the_field_its_type_and_the_value(string row)
    {
        var (field, given) = Refused[row];
        Assert.True(Schema.TryGetEntityType("Priced", out var priced));
        Assert.True(priced.TryGetField(field, out var declared));
        var record = new LatticeRecord(priced);
        var detail = new LatticeOwnedValue(Detail());

        var ofRecord = Assert.Throws<ArgumentException>(() => record[field] = given);
        var ofDetail = Assert.Throws<ArgumentException>(() => detail[field] = given);

        // The words of a value that cannot be used for a field, made as that refusal makes them.
        var expected = $"Field '{field}' is {declared.Declaration.Type}; the {given.GetType().Name} value '{given}' cannot be used for it. (Parameter 'value')";

        foreach (var refusal in new[] { ofRecord, ofDetail })
        {
            Assert.Equal("value", refusal.ParamName);
            Assert.Equal(expected, refusal.Message);
        }

        Assert.Null(record[field]);
        Assert.Null(detail[field]);
    }

    [Fact]
    public void Elements_of_a_collection_are_taken_only_as_declared()
    {
        Assert.True(Schema.TryGetEntityType("Priced", out var priced));
        var record = new LatticeRecord(priced);

        // Integers are accepted for a Decimal given by itself, not for the elements of a List of Decimal.
        record["Price"] = 1;
        Assert.Throws<ArgumentException>(() => record["Prices"] = new[] { 1, 2 });
        Assert.Throws<ArgumentException>(() => record["Prices"] = new List<double> { 1.0 });

        record["Prices"] = new[] { 1m, 2m };
        Assert.Equal([1m, 2m], record.Get<List<decimal>>("Prices"));
    }

    [Fact]
    public async Task Enum_field_refuses_at_the_write_an_integer_its_enum_type_does_not_declare_as_a_field_of_a_record_and_of_an_owned_value()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var priced = database.Set("Priced");

        // Level declares 0 and 1; 7 is held as its integer until the write refuses it.
        var record = new LatticeRecord(priced.EntityType) { ["Code"] = "level", ["Level"] = 7 };
        Assert.Equal((byte)7, record["Level"]);
        await TestData.ExpectValidationFailedAsync("Level", () => priced.InsertAsync(record));

        record["Level"] = 1;
        record["Detail"] = new LatticeOwnedValue(Detail()) { ["Level"] = 7L };
        await TestData.ExpectValidationFailedAsync("Level", () => priced.InsertAsync(record));

        Assert.Null(priced.Get("level"));
    }

    // The value held is of the .NET type of the field and the same number, bit for bit for a floating-point
    // value, the sign of zero included; a NaN is a NaN, whose payload may not be kept.
    private static void ExpectHeld(object expected, object? held)
    {
        Assert.NotNull(held);
        Assert.Equal(expected.GetType(), held.GetType());

        switch (expected)
        {
            case double number when double.IsNaN(number):
                Assert.True(double.IsNaN((double)held));
                break;
            case double number:
                Assert.Equal(BitConverter.DoubleToInt64Bits(number), BitConverter.DoubleToInt64Bits((double)held));
                break;
            case float number when float.IsNaN(number):
                Assert.True(float.IsNaN((float)held));
                break;
            case float number:
                Assert.Equal(BitConverter.SingleToInt32Bits(number), BitConverter.SingleToInt32Bits((float)held));
                break;
            default:
                Assert.Equal(expected, held);
                break;
        }
    }

    private static LatticeOwnedType Detail()
    {
        Assert.True(Schema.TryGetOwnedType("Detail", out var detail));

        return detail;
    }

    private static string Show(object value) => value switch
    {
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}
