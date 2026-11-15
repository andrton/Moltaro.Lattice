using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// What RootLayout.Compile refuses (brief of S05, "The fixed part" and "What Compile does not lay
// out yet"). A fixed part that does not fit one block, Arena.MaxBlockSize bytes with its header,
// is refused with InvalidSchema and the entity type named, when the layout is compiled and so
// before any data is admitted (milestone contract, "Native layout and allocation"). A field S05
// cannot store is NotSupportedException with the field named. An entity type that is not of the
// given schema is ArgumentException.
public sealed class LayoutRefusalTests
{
    [Fact(Skip = NotImplemented.RootLayout)]
    public void The_largest_fixed_part_that_fits_one_block_compiles()
    {
        // 32 + 8 + 16 × 4,093 + 8 = 65,536 bytes.
        var schema = WideSchema.Compile("Widest", decimals: 4_093, bytes: 8);
        Assert.True(schema.TryGetEntityType("Widest", out var widest));

        var layout = RootLayout.Compile(widest, schema);

        Assert.Equal(Arena.MaxBlockSize, layout.FixedSize);
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData(4_093, 9)]
    [InlineData(5_000, 0)]
    public void A_fixed_part_above_one_block_is_InvalidSchema_with_the_entity_type_named(int decimals, int bytes)
    {
        var schema = WideSchema.Compile("Oversized", decimals, bytes);
        Assert.True(schema.TryGetEntityType("Oversized", out var oversized));

        var exception = Assert.Throws<LatticeException>(() => RootLayout.Compile(oversized, schema));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Oversized", exception.Message, StringComparison.Ordinal);
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("Home", "'Type': 'Owned', 'OwnedType': 'Address'")]
    [InlineData("Tags", "'Type': 'List', 'Element': { 'Type': 'String' }")]
    [InlineData("Options", "'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Int32' }")]
    [InlineData("Rights", "'Type': 'Set', 'Element': { 'Type': 'Int32' }")]
    [InlineData("Manager", "'Type': 'Reference', 'EntityType': 'Item'")]
    [InlineData("Region", "'Type': 'String', 'StringStorage': 'Dictionary'")]
    public void A_field_that_S05_cannot_store_is_not_supported_with_the_field_named(string name, string declaration)
    {
        // The field stands among fields that S05 stores, so the message has to name the right one.
        const string Address = "{ 'Id': 1, 'Name': 'Address', 'Fields': [ { 'Id': 1, 'Name': 'City', 'Type': 'String' } ] }";
        var fields = $"{{ 'Id': 2, 'Name': 'Login', 'Type': 'String' }}, {{ 'Id': 3, 'Name': '{name}', {declaration} }}, {{ 'Id': 4, 'Name': 'Count', 'Type': 'Int32' }}";
        var schema = ItemSchema.Compile(fields, ownedTypes: Address);
        Assert.True(schema.TryGetEntityType(ItemSchema.EntityTypeName, out var item));

        var exception = Assert.Throws<NotSupportedException>(() => RootLayout.Compile(item, schema));

        Assert.Contains(name, exception.Message, StringComparison.Ordinal);
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void An_entity_type_of_another_schema_is_refused()
    {
        var schema = ItemSchema.Compile("{ 'Id': 2, 'Name': 'Count', 'Type': 'Int32' }");
        var other = LatticeSchema.Compile("""
            { "EntityTypes": [ { "Id": 1, "Name": "Other", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }
            """);
        Assert.True(other.TryGetEntityType("Other", out var foreign));

        Assert.Throws<ArgumentException>(() => RootLayout.Compile(foreign, schema));
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void An_entity_type_belongs_to_the_schema_that_compiled_it_and_not_to_a_copy()
    {
        // The same descriptor compiled twice gives two schemas, each with its own entity types.
        var json = ItemSchema.Json("{ 'Id': 2, 'Name': 'Count', 'Type': 'Int32' }");
        var schema = LatticeSchema.Compile(json);
        var copy = LatticeSchema.Compile(json);
        Assert.True(copy.TryGetEntityType(ItemSchema.EntityTypeName, out var itemOfCopy));

        Assert.Throws<ArgumentException>(() => RootLayout.Compile(itemOfCopy, schema));
    }
}
