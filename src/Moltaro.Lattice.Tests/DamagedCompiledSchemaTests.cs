using System.Text;

namespace Moltaro.Lattice.Tests;

// A compiled schema whose bytes are damaged is refused with InvalidSchema, the code on which a
// caller compiles the schema again. No damage escapes as another exception, and text that is
// not UTF-8 is refused rather than read as other text. A file holds one compiled schema and
// nothing after it; a stream is left right after the schema.
public sealed class DamagedCompiledSchemaTests
{
    // Every part of the binary form: an ordinary and a Flags enum type with an alias, an
    // entity type with a class, both text policies, enum fields, a nullable Binary field and
    // two indexes, and an entity type without a class.
    private const string Json = """
        {
          "SchemaVersion": 2,
          "Enums": [
            { "Id": 1, "Name": "Plan", "UnderlyingType": "Int8", "Members": [ { "Name": "Free", "Value": -1, "Canonical": true }, { "Name": "Gratis", "Value": -1 } ] },
            { "Id": 2, "Name": "Rights", "UnderlyingType": "UInt64", "Flags": true, "Members": [ { "Name": "Read", "Value": 1 }, { "Name": "Admin", "Value": 9223372036854775808 } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Account", "ClrType": "Example.Account",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 2, "Name": "Region", "Type": "String", "StringStorage": "Dictionary", "Nullable": true },
                { "Id": 3, "Name": "Plan", "Type": "Enum", "Enum": "Plan" },
                { "Id": 4, "Name": "Rights", "Type": "Enum", "Enum": "Rights", "Nullable": true },
                { "Id": 5, "Name": "Photo", "Type": "Binary", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByRegion", "Fields": [2, 3], "Unique": true }, { "Id": 2, "Name": "ByPlan", "Fields": [3] } ]
            },
            { "Id": 2, "Name": "Note", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Guid" } ], "PrimaryKey": [1] }
          ]
        }
        """;

    [Fact]
    public void Text_with_a_negative_length_is_refused()
    {
        // The start of a schema of the current format with one enum type: magic, format 4,
        // catalog version 1, UTF-8, one enum type with the identity 1, and then the length of
        // its name, -1 as a 7-bit encoded integer.
        byte[] data =
        [
            (byte)'M', (byte)'L', (byte)'S', (byte)'C',
            4, 0, 0, 0,
            1, 0, 0, 0,
            1,
            1, 0, 0, 0,
            1, 0, 0, 0,
            0xFF, 0xFF, 0xFF, 0xFF, 0x0F,
        ];

        ExpectRefused(data);
    }

    [Theory]
    // A continuation byte without a start, a byte that UTF-8 never has, a start byte without
    // its continuation.
    [InlineData(0x80)]
    [InlineData(0xFF)]
    [InlineData(0xC3)]
    public void Name_that_is_not_well_formed_UTF8_is_refused_rather_than_replaced(byte value)
    {
        var saved = Save();
        var at = saved.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Account"));
        Assert.True(at >= 0);

        saved[at + 3] = value;

        ExpectRefused(saved);
    }

    [Fact]
    public void Mark_that_is_neither_0_nor_1_is_refused()
    {
        using var stream = new MemoryStream();
        LatticeSchema.Compile("""{ "EntityTypes": [ { "Id": 1, "Name": "N", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }""").Save(stream);
        var saved = stream.ToArray();

        // Magic, format, catalog version, encoding, enum type count, entity type count, the
        // identity and the name of N (length 1, then "N"), then the mark of its class.
        const int ClassMark = 4 + 4 + 4 + 1 + 4 + 4 + 4 + 2;
        Assert.Equal(0, saved[ClassMark]);
        saved[ClassMark] = 2;

        ExpectRefused(saved);
    }

    [Fact]
    public void Every_truncation_of_a_saved_schema_is_refused()
    {
        var saved = Save();

        for (var length = 0; length < saved.Length; length++)
        {
            ExpectRefused(saved[..length]);
        }
    }

    [Fact]
    public void Every_single_byte_change_of_a_saved_schema_loads_or_is_refused()
    {
        var saved = Save();
        var changed = new byte[saved.Length];

        for (var position = 0; position < saved.Length; position++)
        {
            for (var value = 0; value <= byte.MaxValue; value++)
            {
                if (value == saved[position])
                {
                    continue;
                }

                saved.CopyTo(changed, 0);
                changed[position] = (byte)value;

                // A change may give another valid schema, such as another name or another
                // identity; it is never anything but a schema or InvalidSchema.
                var exception = Record.Exception(() => LatticeSchema.Load(new MemoryStream(changed)));
                if (exception is not null and not LatticeException { Error: LatticeErrorEnum.InvalidSchema })
                {
                    Assert.Fail($"Byte {position} changed to {value}: {exception}");
                }
            }
        }
    }

    [Fact]
    public void File_with_data_after_the_compiled_schema_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lattice-{Guid.NewGuid():N}.schema");
        try
        {
            File.WriteAllBytes(path, [.. Save(), 0]);

            TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Stream_is_left_right_after_the_compiled_schema()
    {
        var saved = Save();
        using var stream = new MemoryStream([.. saved, 42]);

        LatticeSchema.Load(stream);

        // What follows the schema belongs to the caller.
        Assert.Equal(saved.Length, stream.Position);
        Assert.Equal(42, stream.ReadByte());
    }

    private static byte[] Save()
    {
        using var stream = new MemoryStream();
        LatticeSchema.Compile(Json).Save(stream);
        return stream.ToArray();
    }

    private static void ExpectRefused(byte[] data) =>
        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(new MemoryStream(data)));
}
