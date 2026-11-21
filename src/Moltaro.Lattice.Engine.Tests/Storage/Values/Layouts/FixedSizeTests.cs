using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// The fixed part of a root (brief of S05, "The fixed part"; milestone contract, "Native layout and
// allocation"). FixedSize is the 32 bytes of the block header, a null bitmap of n / 8 bytes,
// rounded up, for the n nullable fields, the fixed fields at the widths of the scalar catalog
// (Q03.1, Q03.4.1) without padding, and one 16-byte descriptor for every String and Binary field.
// A Boolean is one byte; an Enum has the width of its underlying type; a nullable fixed field keeps
// its width. The expected sizes are added up here from that formula, never read from the layout.
// Every entity type of these tests has the key Id, an Int64, so its fixed part starts with 32 + 8
// bytes.
public sealed class FixedSizeTests
{
    private const int HeaderAndKey = 32 + 8;

    [Fact]
    public void An_entity_type_without_a_nullable_field_has_no_null_bitmap()
    {
        Assert.Equal(HeaderAndKey, FixedSizeOf(""));
        Assert.Equal(HeaderAndKey + 4, FixedSizeOf("{ 'Id': 2, 'Name': 'Count', 'Type': 'Int32' }"));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(16, 2)]
    [InlineData(17, 3)]
    public void The_null_bitmap_has_one_bit_for_every_nullable_field_in_whole_bytes(int nullableFields, int bitmapBytes)
    {
        // Every nullable field is an Int32, which keeps its four bytes whether it is null or not.
        var fields = Enumerable.Range(0, nullableFields)
            .Select(index => $"{{ 'Id': {2 + index}, 'Name': 'Field{index}', 'Type': 'Int32', 'Nullable': true }}");

        Assert.Equal(HeaderAndKey + bitmapBytes + 4 * nullableFields, FixedSizeOf(string.Join(", ", fields)));
    }

    [Theory]
    [InlineData("Boolean", 1)]
    [InlineData("Int8", 1)]
    [InlineData("UInt8", 1)]
    [InlineData("Int16", 2)]
    [InlineData("UInt16", 2)]
    [InlineData("Int32", 4)]
    [InlineData("UInt32", 4)]
    [InlineData("Int64", 8)]
    [InlineData("UInt64", 8)]
    [InlineData("Float32", 4)]
    [InlineData("Float64", 8)]
    [InlineData("Decimal", 16)]
    [InlineData("Guid", 16)]
    [InlineData("DateTimeOffset", 8)]
    [InlineData("DateOnly", 4)]
    [InlineData("TimeOnly", 8)]
    [InlineData("TimeSpan", 8)]
    [InlineData("String", 16)]
    [InlineData("Binary", 16)]
    public void A_field_takes_the_width_of_its_type_in_the_scalar_catalog_and_a_String_or_Binary_one_descriptor(string type, int width)
    {
        Assert.Equal(HeaderAndKey + width, FixedSizeOf($"{{ 'Id': 2, 'Name': 'Value', 'Type': '{type}' }}"));

        // Nullable, the field takes the same width, and the bitmap one byte.
        Assert.Equal(HeaderAndKey + 1 + width, FixedSizeOf($"{{ 'Id': 2, 'Name': 'Value', 'Type': '{type}', 'Nullable': true }}"));
    }

    [Theory]
    [InlineData("Int8", 1)]
    [InlineData("UInt8", 1)]
    [InlineData("Int16", 2)]
    [InlineData("UInt16", 2)]
    [InlineData("Int32", 4)]
    [InlineData("UInt32", 4)]
    [InlineData("Int64", 8)]
    [InlineData("UInt64", 8)]
    public void An_Enum_field_takes_the_width_of_its_underlying_type(string underlyingType, int width)
    {
        foreach (var flags in new[] { false, true })
        {
            var enums = $"{{ 'Id': 1, 'Name': 'Code', 'UnderlyingType': '{underlyingType}', 'Flags': {(flags ? "true" : "false")}, 'Members': [ {{ 'Name': 'One', 'Value': 1 }} ] }}";

            Assert.Equal(HeaderAndKey + width, FixedSizeOf("{ 'Id': 2, 'Name': 'Code', 'Type': 'Enum', 'Enum': 'Code' }", enums));
        }
    }

    [Fact]
    public void Small_integers_are_not_widened()
    {
        // Ten one-byte and five two-byte integers take 20 bytes, not fifteen slots of eight.
        var bytes = Enumerable.Range(0, 10).Select(index => $"{{ 'Id': {2 + index}, 'Name': 'Byte{index}', 'Type': '{(index % 2 == 0 ? "Int8" : "UInt8")}' }}");
        var shorts = Enumerable.Range(0, 5).Select(index => $"{{ 'Id': {12 + index}, 'Name': 'Short{index}', 'Type': '{(index % 2 == 0 ? "Int16" : "UInt16")}' }}");

        Assert.Equal(HeaderAndKey + 10 + 10, FixedSizeOf(string.Join(", ", bytes.Concat(shorts))));
    }

    [Fact]
    public void Every_String_and_Binary_field_takes_one_descriptor_nullable_or_not()
    {
        const string Fields = """
            { 'Id': 2, 'Name': 'Login', 'Type': 'String' },
            { 'Id': 3, 'Name': 'Nickname', 'Type': 'String', 'Nullable': true },
            { 'Id': 4, 'Name': 'Note', 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase' },
            { 'Id': 5, 'Name': 'Photo', 'Type': 'Binary' },
            { 'Id': 6, 'Name': 'Thumbnail', 'Type': 'Binary', 'Nullable': true }
            """;

        // Two nullable fields: one byte of bitmap.
        Assert.Equal(HeaderAndKey + 1 + 5 * 16, FixedSizeOf(Fields));
    }

    [Fact]
    public void The_fixed_part_adds_up_the_header_the_bitmap_the_fixed_fields_and_the_descriptors()
    {
        const string Fields = """
            { 'Id': 2, 'Name': 'IsActive', 'Type': 'Boolean', 'Nullable': true },
            { 'Id': 3, 'Name': 'Name', 'Type': 'String' },
            { 'Id': 4, 'Name': 'Count', 'Type': 'Int32' },
            { 'Id': 5, 'Name': 'Photo', 'Type': 'Binary', 'Nullable': true },
            { 'Id': 6, 'Name': 'Score', 'Type': 'Float64', 'Nullable': true },
            { 'Id': 7, 'Name': 'Code', 'Type': 'Int8' },
            { 'Id': 8, 'Name': 'Born', 'Type': 'DateOnly' },
            { 'Id': 9, 'Name': 'Key', 'Type': 'Guid' }
            """;

        // Header 32, bitmap 1 for three nullable fields, Id 8, Boolean 1, Int32 4, Float64 8, Int8 1,
        // DateOnly 4, Guid 16, two descriptors 32.
        Assert.Equal(32 + 1 + 8 + 1 + 4 + 8 + 1 + 4 + 16 + 32, FixedSizeOf(Fields));
    }

    private static int FixedSizeOf(string fields, string enums = "")
    {
        var schema = ItemSchema.Compile(fields, enums: enums);
        Assert.True(schema.TryGetEntityType(ItemSchema.EntityTypeName, out var item));

        return RootLayout.Compile(item, schema).FixedSize;
    }
}
